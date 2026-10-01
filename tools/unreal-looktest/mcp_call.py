"""Minimal MCP client for the Unreal MCP server (Streamable HTTP, JSON-RPC 2.0). stdlib only.

    python mcp_call.py [--url URL] [--timeout S] [--session-file F] <command> ...

    init                                    initialize a fresh session, print server info + session id
    tools                                   tools/list (tool search is on: list_toolsets, describe_toolset, call_tool)
    toolsets                                list_toolsets -> JSON {"text": ..., "names": [...]} on stdout
    describe <toolset>                      describe_toolset
    call <toolset> <tool> ['<json>'] [--save-images DIR]
                                            call_tool; use "-" as toolset for a top-level tool
    raw <method> ['<json params>']          any JSON-RPC method, prints the raw result

Protocol facts (read from Engine/Plugins/Experimental/ModelContextProtocol, UE 5.8.3):
  * POST <url> with a JSON-RPC body. The server answers application/json; text/event-stream only
    when the request carries _meta.progressToken (this client never sends one, but parses both).
  * initialize creates a session and returns it in the Mcp-Session-Id response header. Every other
    method needs that header (missing -> 400, unknown -> 404 "client should reinitialize").
    notifications/initialized must follow initialize (answered 202, empty body).
  * Mcp-Protocol-Version, if sent, must equal the negotiated version (2025-11-25).
  * In tool-search mode call_tool returns the toolset result as ONE text block holding JSON, so an
    image (FToolsetImage {mimeType, data}) arrives nested in that text, not as a type:"image" block.
    Both shapes are decoded by --save-images.
Exit code 0 on success, 1 on any error (connection, JSON-RPC error, isError result, no image found).
"""
import argparse
import base64
import datetime
import json
import os
import re
import sys
import urllib.error
import urllib.request

LT = 'D:/Projects/Chimera-Unreal/ProjectChimera/LookTest'
DEFAULT_URL = 'http://127.0.0.1:8000/mcp'
SESSION_FILE = LT + '/run/mcp_session.json'
PROTOCOL = '2025-11-25'
CLIENT_INFO = {'name': 'chimera-looktest-mcp_call', 'version': '1.0'}

_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))  # loopback: never via a proxy
_NEXT_ID = [0]


class McpError(Exception):
    """Any failure talking to the server; carries the HTTP status when there was one."""

    def __init__(self, msg, status=None, body=''):
        super().__init__(msg)
        self.status = status
        self.body = body


# ---------------------------------------------------------------- transport

def post(url, body, session_id=None, protocol=None, timeout=120):
    """POST one JSON-RPC message. Returns (status, response_headers, content_type, text); raises McpError on no response."""
    headers = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'}
    if session_id:
        headers['Mcp-Session-Id'] = session_id
    if protocol:
        headers['Mcp-Protocol-Version'] = protocol
    req = urllib.request.Request(url, data=json.dumps(body).encode('utf-8'), headers=headers, method='POST')
    try:
        with _OPENER.open(req, timeout=timeout) as r:
            return r.status, r.headers, r.headers.get('Content-Type', ''), r.read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return e.code, e.headers, e.headers.get('Content-Type', ''), e.read().decode('utf-8', 'replace')
    except (urllib.error.URLError, ConnectionError, TimeoutError, OSError) as e:
        raise McpError(f'cannot reach {url}: {e} (is the editor running with -ModelContextProtocolStartServer?)')


def sse_messages(text):
    """Yield the parsed JSON of every SSE event's data payload."""
    for block in re.split(r'\r?\n\r?\n', text):
        data = []
        for line in block.splitlines():
            if line.startswith('data:'):
                data.append(line[5:][1:] if line[5:6] == ' ' else line[5:])
        if data:
            try:
                yield json.loads('\n'.join(data))
            except json.JSONDecodeError:
                continue  # a non-JSON keep-alive payload


def parse_response(content_type, text, req_id):
    """Return the JSON-RPC response object matching req_id from a JSON or SSE body (None for an empty body)."""
    if not text.strip():
        return None
    if 'text/event-stream' in content_type.lower():
        for msg in sse_messages(text):
            if isinstance(msg, dict) and msg.get('id') == req_id and ('result' in msg or 'error' in msg):
                return msg
        raise McpError(f'SSE body had no response for id {req_id}: {text[:300]!r}')
    try:
        return json.loads(text)
    except json.JSONDecodeError as e:
        raise McpError(f'response is not JSON ({e}): {text[:300]!r}')


# ---------------------------------------------------------------- session

def load_session(session_file, url):
    """Return the saved (session_id, protocol) for this url, or (None, None)."""
    try:
        with open(session_file, encoding='utf-8') as f:
            s = json.load(f)
        if s.get('url') == url and s.get('session_id'):
            return s['session_id'], s.get('protocol') or PROTOCOL
    except (OSError, json.JSONDecodeError):
        pass
    return None, None


def save_session(session_file, url, sid, protocol):
    os.makedirs(os.path.dirname(session_file), exist_ok=True)
    tmp = session_file + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump({'url': url, 'session_id': sid, 'protocol': protocol,
                   'saved': datetime.datetime.now().isoformat(timespec='seconds')}, f, indent=1)
    os.replace(tmp, session_file)


def new_session(url, session_file, timeout):
    """initialize + notifications/initialized. Returns (session_id, protocol, initialize_result)."""
    _NEXT_ID[0] += 1
    rid = _NEXT_ID[0]
    body = {'jsonrpc': '2.0', 'id': rid, 'method': 'initialize',
            'params': {'protocolVersion': PROTOCOL, 'capabilities': {}, 'clientInfo': CLIENT_INFO}}
    status, hdrs, ctype, text = post(url, body, timeout=timeout)
    if status != 200:
        raise McpError(f'initialize failed: HTTP {status}: {text[:300]}', status, text)
    msg = parse_response(ctype, text, rid)
    if not msg or 'result' not in msg:
        raise McpError(f'initialize returned no result: {text[:300]}')
    sid = hdrs.get('Mcp-Session-Id')
    if not sid:
        raise McpError('initialize response carried no Mcp-Session-Id header')
    protocol = msg['result'].get('protocolVersion', PROTOCOL)
    status, _, _, text = post(url, {'jsonrpc': '2.0', 'method': 'notifications/initialized'}, sid, protocol, timeout)
    if status not in (200, 202, 204):
        raise McpError(f'notifications/initialized failed: HTTP {status}: {text[:300]}', status, text)
    save_session(session_file, url, sid, protocol)
    return sid, protocol, msg['result']


def _is_session_error(status, text):
    """404 = unknown session; 400 only counts when the server is complaining about the session header."""
    return status == 404 or (status == 400 and 'session' in text.lower())


def rpc(method, params, url, session_file, timeout):
    """Send one request on the saved session (creating or re-creating it as needed). Returns the result object."""
    sid, protocol = load_session(session_file, url)
    for attempt in (0, 1):
        if not sid:
            sid, protocol, _ = new_session(url, session_file, timeout)
        _NEXT_ID[0] += 1
        rid = _NEXT_ID[0]
        body = {'jsonrpc': '2.0', 'id': rid, 'method': method}
        if params is not None:
            body['params'] = params
        status, _, ctype, text = post(url, body, sid, protocol, timeout)
        if _is_session_error(status, text) and attempt == 0:
            sid = None  # stale session (editor restarted): initialize again and retry once
            continue
        msg = None
        try:
            msg = parse_response(ctype, text, rid)
        except McpError:
            if status == 200:
                raise
        if msg and 'error' in msg:
            raise McpError(f'{method}: JSON-RPC error {msg["error"].get("code")}: {msg["error"].get("message")}', status, text)
        if status != 200 or not msg or 'result' not in msg:
            raise McpError(f'{method}: HTTP {status}: {text[:300]}', status, text)
        return msg['result']
    raise McpError(f'{method}: session could not be established')


# ---------------------------------------------------------------- result decoding

def find_images(node, found):
    """Collect {mimeType, data} dicts anywhere in a JSON value (FToolsetImage as serialised by the toolset registry)."""
    if isinstance(node, dict):
        mime = next((v for k, v in node.items() if k.lower() in ('mimetype', 'mime_type')), None)
        data = node.get('data') if 'data' in node else node.get('Data')
        if isinstance(mime, str) and mime.startswith('image/') and isinstance(data, str) and data:
            found.append((mime, data))
        for v in node.values():
            find_images(v, found)
    elif isinstance(node, list):
        for v in node:
            find_images(v, found)


def elide_images(node):
    """Return a copy of a JSON value with image base64 payloads replaced by a size note."""
    if isinstance(node, dict):
        out = {}
        mime = next((v for k, v in node.items() if k.lower() in ('mimetype', 'mime_type')), None)
        is_img = isinstance(mime, str) and mime.startswith('image/')
        for k, v in node.items():
            out[k] = f'<base64 {len(v)} chars>' if is_img and k.lower() == 'data' and isinstance(v, str) else elide_images(v)
        return out
    if isinstance(node, list):
        return [elide_images(v) for v in node]
    return node


def save_image(mime, b64, out_dir, stem, index):
    """Decode one base64 image to a file in out_dir and return its path."""
    raw = base64.b64decode(b64, validate=False)
    ext = {'image/png': 'png', 'image/jpeg': 'jpg', 'image/webp': 'webp'}.get(mime, 'bin')
    if ext == 'png' and raw[:8] != b'\x89PNG\r\n\x1a\n':
        raise McpError(f'image {index} is labelled image/png but is not a PNG ({len(raw)} bytes)')
    os.makedirs(out_dir, exist_ok=True)
    now = datetime.datetime.now()
    base = f'{out_dir}/{now:%Y%m%d-%H%M%S}-{now.microsecond // 1000:03d}_{stem}_{index}'
    n = 0
    while True:  # 'xb' never overwrites: a capture in the same millisecond gets a -N suffix
        path = f'{base}{"-" + str(n) if n else ""}.{ext}'
        try:
            with open(path, 'xb') as f:
                f.write(raw)
            return path
        except FileExistsError:
            n += 1


def render_tool_result(result, save_dir, stem):
    """Print text blocks (images elided), save images if asked. Returns True when the result is not an error."""
    images = []
    for block in result.get('content', []):
        kind = block.get('type')
        if kind == 'image':
            images.append((block.get('mimeType', 'image/png'), block.get('data', '')))
        elif kind == 'text':
            text = block.get('text', '')
            try:
                parsed = json.loads(text)
            except json.JSONDecodeError:
                print(text)
                continue
            find_images(parsed, images)
            print(json.dumps(elide_images(parsed), indent=1))
        else:
            print(json.dumps(block)[:500])
    if 'structuredContent' in result:
        find_images(result['structuredContent'], images)
    # structuredContent usually repeats the text block, so de-duplicate identical payloads
    unique = list(dict.fromkeys(images))
    if save_dir:
        if not unique:
            raise McpError('--save-images given but the result contained no image')
        for i, (mime, data) in enumerate(unique):
            print('IMAGE ' + save_image(mime, data, save_dir, stem, i))
    return not result.get('isError', False)


# ---------------------------------------------------------------- commands

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--url', default=DEFAULT_URL)
    ap.add_argument('--timeout', type=float, default=120)
    ap.add_argument('--session-file', default=SESSION_FILE)
    sub = ap.add_subparsers(dest='cmd', required=True)
    sub.add_parser('init')
    sub.add_parser('tools')
    sub.add_parser('toolsets')
    d = sub.add_parser('describe')
    d.add_argument('toolset')
    c = sub.add_parser('call')
    c.add_argument('toolset', help='toolset name, or "-" for a top-level tool')
    c.add_argument('tool')
    c.add_argument('args', nargs='?', default='{}', help='JSON object of tool arguments')
    c.add_argument('--save-images', metavar='DIR')
    r = sub.add_parser('raw')
    r.add_argument('method')
    r.add_argument('params', nargs='?', default=None)
    a = ap.parse_args()

    def go(method, params):
        return rpc(method, params, a.url, a.session_file, a.timeout)

    try:
        if a.cmd == 'init':
            sid, protocol, res = new_session(a.url, a.session_file, a.timeout)
            print(json.dumps({'serverInfo': res.get('serverInfo'), 'protocolVersion': protocol,
                              'session_id': sid, 'capabilities': res.get('capabilities')}, indent=1))
        elif a.cmd == 'tools':
            names, cursor = [], None
            while True:
                res = go('tools/list', {'cursor': cursor} if cursor else {})
                names += [t['name'] for t in res.get('tools', [])]
                cursor = res.get('nextCursor')
                if not cursor:
                    break
            print(json.dumps(names))
        elif a.cmd == 'toolsets':
            res = go('tools/call', {'name': 'list_toolsets', 'arguments': {}})
            text = ''.join(b.get('text', '') for b in res.get('content', []) if b.get('type') == 'text')
            if res.get('isError'):
                raise McpError(f'list_toolsets failed: {text}')
            names = [m.group(1) for m in re.finditer(r'^- ([^:\n]+?)(?::|$)', text, re.M)]
            print(json.dumps({'text': text, 'names': names}, indent=1))
        elif a.cmd == 'describe':
            res = go('tools/call', {'name': 'describe_toolset', 'arguments': {'toolset_name': a.toolset}})
            return 0 if render_tool_result(res, None, 'describe') else 1
        elif a.cmd == 'call':
            try:
                tool_args = json.loads(a.args)
            except json.JSONDecodeError as e:
                raise McpError(f'tool arguments are not valid JSON: {e}')
            inner = {'tool_name': a.tool, 'arguments': tool_args}
            if a.toolset != '-':
                inner['toolset_name'] = a.toolset
            res = go('tools/call', {'name': 'call_tool', 'arguments': inner})
            return 0 if render_tool_result(res, a.save_images, a.tool) else 1
        elif a.cmd == 'raw':
            params = json.loads(a.params) if a.params else None
            print(json.dumps(go(a.method, params), indent=1))
    except McpError as e:
        print(f'mcp_call: {e}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
