# -*- coding: utf-8 -*-
"""
Minimal dependency-free ComfyUI API client for headless generation.
Drives a running ComfyUI server (default 127.0.0.1:8188) over HTTP.

Usage pattern:
    c = ComfyClient()
    c.put_input_image(local_png, "concept_worker.png")   # stages into ComfyUI/input/
    pid = c.queue(workflow_dict)                          # POST /prompt
    hist = c.wait(pid, timeout=900)                       # poll /history
    files = c.output_files(hist)                          # [{type,filename,subfolder}]
    c.copy_outputs(files, dest_dir)                       # pull .glb/.png out of ComfyUI/output

Input images are staged directly into ComfyUI/input (LoadImage reads from there) so we
avoid the multipart /upload endpoint. Outputs land in ComfyUI/output/<subfolder>.
"""
import json, time, os, shutil, urllib.request, urllib.error, uuid

class ComfyClient:
    def __init__(self, host="127.0.0.1:8188", comfy_root=None):
        self.host = host
        self.cid = uuid.uuid4().hex
        # ComfyUI/ root that holds input/ and output/
        self.comfy_root = comfy_root or os.environ.get("COMFY_ROOT", "")

    # ---- server probes ----
    def _get(self, path):
        with urllib.request.urlopen(f"http://{self.host}{path}", timeout=30) as r:
            return json.loads(r.read().decode())

    def ping(self):
        try:
            self._get("/system_stats"); return True
        except Exception:
            return False

    def object_info(self, node=None):
        return self._get("/object_info" + (f"/{node}" if node else ""))

    # ---- input staging ----
    def put_input_image(self, local_path, name):
        dst = os.path.join(self.comfy_root, "input", name)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy(local_path, dst)
        return name

    # ---- queue + wait ----
    def queue(self, workflow):
        body = json.dumps({"prompt": workflow, "client_id": self.cid}).encode()
        req = urllib.request.Request(f"http://{self.host}/prompt", data=body,
                                     headers={"Content-Type": "application/json"})
        try:
            resp = json.loads(urllib.request.urlopen(req, timeout=60).read().decode())
        except urllib.error.HTTPError as e:
            raise RuntimeError(f"/prompt rejected: {e.read().decode()[:800]}")
        if "prompt_id" not in resp:
            raise RuntimeError(f"no prompt_id in response: {resp}")
        return resp["prompt_id"]

    def wait(self, prompt_id, timeout=900, poll=2.0):
        start = time.time()
        while time.time() - start < timeout:
            hist = self._get(f"/history/{prompt_id}")
            if prompt_id in hist:
                entry = hist[prompt_id]
                status = entry.get("status", {})
                if status.get("status_str") == "error" or status.get("completed") is False and status.get("status_str"):
                    # surface node errors
                    msgs = status.get("messages", [])
                    raise RuntimeError(f"workflow error: {json.dumps(msgs)[:1200]}")
                if status.get("completed") or entry.get("outputs"):
                    return entry
            time.sleep(poll)
        raise TimeoutError(f"prompt {prompt_id} did not finish in {timeout}s")

    # ---- outputs ----
    def output_files(self, hist_entry):
        out = []
        for node_id, data in (hist_entry.get("outputs") or {}).items():
            for key, items in data.items():
                if isinstance(items, list):
                    for it in items:
                        if isinstance(it, dict) and it.get("filename"):
                            out.append({"node": node_id, "kind": key,
                                        "filename": it["filename"],
                                        "subfolder": it.get("subfolder", ""),
                                        "type": it.get("type", "output")})
        return out

    def copy_outputs(self, files, dest_dir):
        os.makedirs(dest_dir, exist_ok=True)
        copied = []
        for f in files:
            src = os.path.join(self.comfy_root, f.get("type", "output"), f["subfolder"], f["filename"])
            if os.path.exists(src):
                dst = os.path.join(dest_dir, f["filename"])
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copy(src, dst)
                copied.append(dst)
        return copied


if __name__ == "__main__":
    import sys
    c = ComfyClient(comfy_root=sys.argv[1] if len(sys.argv) > 1 else "")
    print("ping:", c.ping())
