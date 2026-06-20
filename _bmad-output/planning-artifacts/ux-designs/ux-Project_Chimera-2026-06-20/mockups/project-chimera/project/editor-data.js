/* ============================================================
   CREATION SUITE — data + interactions
   ============================================================ */

/* ---------- field builder ---------- */
function fieldRow(label, tip, min, max, val, adv){
  return `<div class="fieldrow${adv?' adv-only':''}">
    <label>${label}<svg class="qm" viewBox="0 0 14 14"><circle cx="7" cy="7" r="6" fill="none" stroke="currentColor" stroke-width="1.2"/><path d="M5.5 5.4a1.6 1.6 0 113 .6c0 1-1.5 1.2-1.5 2.2" stroke="currentColor" stroke-width="1.1" fill="none"/><circle cx="7" cy="10.6" r=".7" fill="currentColor"/></svg>
      <span class="f-tip"><b>${label}</b> — ${tip}</span></label>
    <input type="range" class="slider" min="${min}" max="${max}" value="${val}" data-key="${label}">
    <input class="num-input" value="${val}"><div class="minmax" style="grid-column:3;display:none"></div></div>`;
}

const combat = [
  ['Hit Points','Total health before the unit dies.',1,2000,820],
  ['Attack Damage','Damage dealt per hit, before armor.',0,200,46],
  ['Attack Range','0 = melee. Tiles the unit can strike from.',0,12,1],
  ['Attack Speed','Cooldown between attacks, lower = faster.',0,300,120],
  ['Armor','Flat damage reduction per hit.',0,20,4],
  ['Move Speed','World units travelled per second.',0,500,270],
];
const combatAdv = [
  ['Vision Range','Sight radius — feeds fog-of-war reveal.',0,16,9],
  ['Collision Size','Footprint radius for pathing.',0,40,12],
  ['Armor Type','Light / Heavy / Fortified damage table.',0,2,1],
  ['Damage Type','Normal / Piercing / Siege / Magic.',0,3,0],
];
const econ = [
  ['Ore Cost','Primary resource to train this unit.',0,500,50],
  ['Crystal Cost','Secondary resource. 0 if none.',0,300,0],
  ['Build Time','Seconds to produce at base rate.',1,90,18],
  ['Supply Cost','Population this unit consumes.',0,10,1],
];

document.getElementById('combatFields').innerHTML =
  combat.map(f=>fieldRow(...f)).join('') + combatAdv.map(f=>fieldRow(f[0],f[1],f[2],f[3],f[4],true)).join('');
document.getElementById('econFields').innerHTML = econ.map(f=>fieldRow(...f)).join('');

/* ---------- abilities ---------- */
const ABIL_ICON = {
  charge:'<path d="M9 1L3 9h4l-1 6 7-9H8z" fill="currentColor"/>',
  shield:'<path d="M8 1l6 3v5c0 3-2.5 5-6 6-3.5-1-6-3-6-6V4z" fill="none" stroke="currentColor" stroke-width="1.3"/>',
  volley:'<path d="M2 14L14 2M14 2h-4M14 2v4M6 6l-1 3 3-1" stroke="currentColor" stroke-width="1.3" fill="none"/>',
};
let abilities = [
  {n:'Charge',ic:'charge',m:'Active · 12s CD'},
  {n:'Shield Wall',ic:'shield',m:'Active · 18s CD'},
];
function renderAbil(){
  document.getElementById('abilList').innerHTML = abilities.map((a,i)=>`
    <div class="abil"><span class="ab-ic"><svg width="16" height="16" viewBox="0 0 16 16">${ABIL_ICON[a.ic]||ABIL_ICON.charge}</svg></span>
    <div><div class="ab-name">${a.n}</div><div class="ab-meta">${a.m}</div></div>
    <span class="x" onclick="abilities.splice(${i},1);renderAbil();updateJSON()"><svg width="13" height="13" viewBox="0 0 14 14"><path d="M3 3l8 8M11 3l-8 8" stroke="currentColor" stroke-width="1.5"/></svg></span></div>`).join('');
}
function addAbil(){ abilities.push({n:'Volley',ic:'volley',m:'Active · 9s CD'}); renderAbil(); updateJSON(); }
renderAbil();

/* ---------- hero fields ---------- */
document.getElementById('heroFields').innerHTML =
  fieldRow('Max Level','Level cap for this hero.',1,30,10) +
  fieldRow('XP per Level','Base experience needed to level.',50,2000,260) +
  fieldRow('Strength / Lv','Primary stat gain per level.',0,12,3) +
  `<div class="fieldrow"><label>Ultimate<svg class="qm" viewBox="0 0 14 14"><circle cx="7" cy="7" r="6" fill="none" stroke="currentColor" stroke-width="1.2"/></svg></label>
   <select class="select" style="grid-column:2/4"><option>Meteor Strike — AoE burst</option><option>Rally Banner — team buff</option><option>Phase Shift — untargetable 3s</option></select></div>`;

/* ---------- templates ---------- */
const tpls = [['Footman','F'],['Archer','A'],['Worker','W'],['Blank','+']];
document.getElementById('tplGrid').innerHTML = tpls.map((t,i)=>`
  <div class="tpl${i==0?' sel':''}" onclick="pickTpl(this)"><span class="ph ph--facet ti">${t[1]}</span><span class="tn">${t[0]}</span></div>`).join('');
function pickTpl(el){ document.querySelectorAll('.tpl').forEach(x=>x.classList.remove('sel')); el.classList.add('sel'); }

/* ---------- compare ---------- */
const cmpData = [['Hit Points',820,1100],['Attack',46,72],['Armor',4,7],['Speed',270,220],['Ore Cost',50,120],['Build Time',18,34]];
document.getElementById('cmpBody').innerHTML = cmpData.map(([l,a,b])=>{
  const d=b-a, up=d>0; return `<div class="cmp-row"><span class="cl">${l}</span><span class="cv">${a}</span>
  <span class="cd ${up?'up':'down'}">${up?'▲':'▼'}${Math.abs(d)}</span></div>`;}).join('')
  + `<p class="muted" style="font-size:11px;margin-top:12px">Δ shown vs <b style="color:var(--text-mid)">Knight</b>. Green = Vanguard is lower-cost / faster.</p>`;
function toggleCompare(){ document.getElementById('compare').classList.toggle('show'); }

/* ---------- hero toggle ---------- */
function toggleHero(){
  const on=document.getElementById('heroSwitch').classList.toggle('on');
  document.getElementById('heroFields').classList.toggle('show',on);
  updateJSON();
}

/* ---------- JSON escape hatch ---------- */
function updateJSON(){
  const get=l=>{const el=[...document.querySelectorAll('.slider')].find(s=>s.dataset.key===l);return el?+el.value:0;};
  const hero=document.getElementById('heroSwitch').classList.contains('on');
  const j={
    id:"vanguard", name:document.querySelector('.uc-name').value, tier:1,
    combat:{hp:get('Hit Points'),attack:get('Attack Damage'),range:get('Attack Range'),armor:get('Armor'),speed:get('Move Speed')},
    economy:{ore:get('Ore Cost'),crystal:get('Crystal Cost'),buildTime:get('Build Time'),supply:get('Supply Cost')},
    abilities:abilities.map(a=>a.n.toLowerCase().replace(/ /g,'_')),
    hero:hero?{maxLevel:get('Max Level'),xpPerLevel:get('XP per Level')}:false
  };
  const s=JSON.stringify(j,null,2)
    .replace(/"([^"]+)":/g,'<span class="jk">"$1"</span>:')
    .replace(/: (\d+)/g,': <span class="jn">$1</span>')
    .replace(/: "([^"]+)"/g,': <span class="jv">"$1"</span>')
    .replace(/: (true|false)/g,': <span class="jn">$1</span>');
  document.getElementById('jsonOut').innerHTML = s;
}

/* ---------- sliders ↔ numeric, live JSON ---------- */
document.addEventListener('input', e=>{
  if(e.target.classList.contains('slider')){
    const n=e.target.parentElement.querySelector('.num-input'); if(n)n.value=e.target.value; updateJSON();
  }
  if(e.target.classList.contains('num-input')){
    const r=e.target.parentElement.querySelector('.slider'); if(r){r.value=e.target.value;updateJSON();}
  }
  if(e.target.classList.contains('uc-name')) updateJSON();
});
updateJSON();

/* ---------- Simple / Advanced ---------- */
function setDisclosure(mode){
  const adv = mode==='advanced';
  document.getElementById('uc').closest('.stage').classList.toggle('is-advanced',adv);
  document.querySelectorAll('#simpleAdv button').forEach((b,i)=>b.classList.toggle('is-active', (i===1)===adv));
  // also drive min/max visibility
  document.querySelectorAll('.minmax').forEach(m=>{m.style.display=adv?'block':'none';
    const s=m.parentElement.querySelector('.slider'); if(s)m.textContent=s.min+'–'+s.max;});
}

/* ---------- EDIT / PLAY ---------- */
function setMode(mode){
  const play = mode==='play';
  document.getElementById('playmode').classList.toggle('show',play);
  document.querySelectorAll('#editplay button').forEach(b=>b.classList.remove('is-active'));
  document.querySelector('#editplay .'+mode).classList.add('is-active');
}
document.addEventListener('keydown',e=>{ if(e.key==='Escape') setMode('edit'); });

/* ---------- PANEL SWITCHING ---------- */
const PANELS = {
  unit:{pv:'unit',title:'Unit Card Editor',eye:'ENTITY · UNIT DEFINITION',adv:true},
  triggers:{pv:'triggers',title:'Triggers / Rules',eye:'LOGIC · EVENT → CONDITION → ACTION',adv:false},
  ability:{pv:'ability',title:'Ability Editor',eye:'COMPOSE · EFFECT PRIMITIVES',adv:false},
  terrain:{pv:'terrain',title:'Terrain Brush',eye:'WORLD · SCULPT & PAINT',adv:false},
  ai:{pv:'ai',title:'AI Generate',eye:'ASSIST · PROMPT → EDITABLE RESULT',adv:false},
  faction:{pv:'faction',title:'Faction Definer',eye:'WIZARD · 5 STEPS',adv:false},
  resources:{pv:'generic',title:'Resource Nodes',eye:'WORLD · ORE & CRYSTAL',adv:false,ph:'Resource node placement · Ore / Crystal yields'},
  win:{pv:'generic',title:'Win Conditions',eye:'RULES · OBJECTIVES',adv:false,ph:'Win condition builder · Annihilation / King-of-Hill / Survival'},
  select:{pv:'unit',title:'Unit Card Editor',eye:'ENTITY · UNIT DEFINITION',adv:true},
};
function switchPanel(key){
  const p = PANELS[key]; if(!p) return;
  document.querySelectorAll('.panel-view').forEach(v=>v.classList.remove('show'));
  document.getElementById('pv-'+p.pv).classList.add('show');
  document.getElementById('dockTitle').textContent=p.title;
  document.getElementById('dockEyebrow').textContent=p.eye;
  document.getElementById('simpleAdv').style.visibility = p.adv?'visible':'hidden';
  if(p.ph) document.getElementById('genPh').textContent=p.ph;
  // sync active states across palette + toolbar
  document.querySelectorAll('.pal-btn').forEach(b=>b.classList.toggle('is-active',b.dataset.panel===key));
  document.querySelectorAll('.tb-tool').forEach(b=>b.classList.toggle('is-active',b.dataset.panel===key));
}
document.getElementById('palette').addEventListener('click',e=>{const b=e.target.closest('.pal-btn');if(b)switchPanel(b.dataset.panel);});
document.getElementById('tbTools').addEventListener('click',e=>{const b=e.target.closest('.tb-tool');if(b)switchPanel(b.dataset.panel);});

/* ---------- triggers list/graph ---------- */
function setTrigView(v){
  const graph=v==='graph';
  document.getElementById('trigMain').classList.toggle('hide',graph);
  document.getElementById('trigGraph').classList.toggle('show',graph);
  document.querySelectorAll('#trigView button').forEach((b,i)=>b.classList.toggle('is-active',(i===1)===graph));
}

/* ---------- ability editor content ---------- */
document.getElementById('abilCfg').innerHTML =
  `<div class="fieldrow"><label>Target</label><select class="select" style="grid-column:2/4"><option>Point (AoE)</option><option>Single enemy</option><option>Self</option></select></div>`+
  fieldRow('Mana Cost','Resource spent per cast.',0,150,35)+
  fieldRow('Cooldown','Seconds before recast.',0,60,9)+
  fieldRow('Cast Range','Max distance to target.',0,15,7);
const prims=[['Deal Damage','40 magic in 2.5 radius','volley'],['Apply Slow','-30% speed for 3s','shield'],['Spawn VFX','arrow_rain.vfx','charge']];
document.getElementById('primList').innerHTML = prims.map(p=>`
  <div class="abil"><span class="ab-ic"><svg width="16" height="16" viewBox="0 0 16 16">${ABIL_ICON[p[2]]}</svg></span>
  <div><div class="ab-name">${p[0]}</div><div class="ab-meta">${p[1]}</div></div>
  <span class="x"><svg width="13" height="13" viewBox="0 0 14 14"><path d="M3 3l8 8M11 3l-8 8" stroke="currentColor" stroke-width="1.5"/></svg></span></div>`).join('');

/* ---------- terrain ---------- */
const brushes=[['Raise','M3 14l5-7 4 4'],['Lower','M3 7l5 7 4-4'],['Flatten','M3 11h12'],['Smooth','M3 11c3-6 6 0 9-3'],['Ramp','M3 14l8-8h2'],['Cliff','M3 14V6h5v8'],['Noise','M3 10l2-2 2 3 2-4 2 3 2-2'],['Paint','M4 13l6-6 3 3-6 6z']];
document.getElementById('brushGrid').innerHTML = brushes.map((b,i)=>`
  <div class="brush${i==0?' sel':''}" onclick="document.querySelectorAll('.brush').forEach(x=>x.classList.remove('sel'));this.classList.add('sel')">
  <svg viewBox="0 0 16 18"><path d="${b[1]}" fill="none" stroke="currentColor" stroke-width="1.4"/></svg><span>${b[0]}</span></div>`).join('');
const texCols=['#3a6e4a','#7a6b3a','#5a5550','#43504e','#6e5240','#2f4a55'];
document.getElementById('texGrid').innerHTML = texCols.map((c,i)=>`<div class="tex${i==0?' sel':''}" style="background:${c}" onclick="document.querySelectorAll('.tex').forEach(x=>x.classList.remove('sel'));this.classList.add('sel')"></div>`).join('');
document.getElementById('doodadGrid').innerHTML = [['Tree','M8 3l3 5H5z M8 6l4 6H4z'],['Rock','M3 13l3-6 5 1 2 5z'],['Crystal','M8 2l3 6-3 8-3-8z'],['Ruin','M3 14V7h2v3h2V7h2v3h2V7h2v7']].map(d=>`
  <div class="brush"><svg viewBox="0 0 16 16"><path d="${d[1]}" fill="none" stroke="currentColor" stroke-width="1.3"/></svg><span>${d[0]}</span></div>`).join('');

/* ---------- AI generate ---------- */
function aiGen(){
  const r=document.getElementById('aiResult');
  r.innerHTML='<div style="display:flex;flex-direction:column;align-items:center;gap:14px;padding:26px 0">'+
    '<span class="tmute" role="status" aria-label="Generating"><svg class="tm-ring"><use href="#chimera-spin-ring"></use></svg><svg class="tm-tri"><use href="#chimera-spin-tri"></use></svg><svg class="tm-core"><use href="#chimera-spin-core"></use></svg></span>'+
    '<span class="muted" style="font-size:12px;font-family:var(--font-mono);letter-spacing:.08em;text-transform:uppercase">Transmuting…</span></div>'+
    '<div class="ai-card shimmer" style="height:90px"></div><div class="ai-card shimmer" style="height:90px"></div>';
  setTimeout(()=>{
    const units=[['Dune Strider','HP 420 · ATK 38 · SPD 360','Glass-cannon raider'],['Sand Skiff','HP 600 · ATK 22 · SPD 300','Mobile harasser'],['Mirage Caller','HP 340 · ATK 30 · SPD 280','Decoy summoner']];
    r.innerHTML = `<div class="row between" style="margin-bottom:12px"><span class="eyebrow">Generated · editable</span><button class="btn btn-ghost btn-sm">Regenerate all</button></div>`+
    units.map(u=>`<div class="ai-card"><div class="ach"><span class="ph ph--facet" style="width:30px;height:30px;font-size:8px">U</span><span class="nm">${u[0]}</span><span class="tag tag--accent" style="margin-left:auto">${u[2]}</span></div>
      <div class="ai-chips"><span class="chip mono" style="font-size:11px">${u[1]}</span></div>
      <div class="row gap2" style="margin-top:10px"><button class="btn btn-primary btn-sm">Add to roster</button><button class="btn btn-ghost btn-sm">Edit in Unit Card →</button></div></div>`).join('');
  },1100);
}

/* ---------- faction wizard ---------- */
const wsteps=['Name & Color','Unit Roster','Buildings & Tech','Start Conditions','AI Preset'];
let wcur=1;
function renderWiz(){
  document.getElementById('wizSteps').innerHTML = wsteps_html();
  document.getElementById('wizCount').textContent=`Step ${wcur+1} / 5`;
  document.getElementById('wizBody').innerHTML = wizContent(wcur);
}
function wsteps_html(){return wsteps_map();}
function wsteps_map(){
  return wsteps_arr();
}
function wsteps_arr(){
  return wsteps_render();
}
function wsteps_render(){
  return wstepsHTML();
}
function wstepsHTML(){
  return wstepsBuild();
}
function wstepsBuild(){
  return wstepsFinal();
}
function wstepsFinal(){
  return ['Name & Color','Unit Roster','Buildings & Tech','Start Conditions','AI Preset'].map((s,i)=>{
    const cls=i<wcur?'done':i===wcur?'active':'';
    return `<div class="wstep ${cls}"><div class="wn">${i<wcur?'✓':i+1}</div><div class="wl">${s}</div></div>`;
  }).join('');
}
function wizContent(i){
  if(i===0) return `<div class="field" style="max-width:340px;margin-bottom:18px"><label>Faction Name</label><input class="input" value="The Ashlanders"></div>
    <div class="field"><label>Team Color (colorblind-safe set)</label><div class="colorpick">
    ${['--team-1','--team-2','--team-3','--team-4','--team-6','--team-7'].map((c,k)=>`<span class="cp${k==1?' sel':''}" style="background:var(${c})"></span>`).join('')}</div></div>`;
  if(i===1) return `<p class="muted" style="font-size:13px;margin-bottom:14px">Pick the roster. Each opens in the consolidated Unit Card Editor.</p>
    <div class="roster-grid">${[['Dune Strider','Tier 1'],['Sand Skiff','Tier 1'],['Mirage Caller','Tier 2'],['War Wagon','Tier 2'],['Sky Serpent','Tier 3'],['Add unit','+']].map(u=>`
    <div class="tpl"><span class="ph ph--facet ti">${u[1]==='+'?'+':'U'}</span><div><div class="tn">${u[0]}</div><div class="muted" style="font-size:10px">${u[1]}</div></div></div>`).join('')}</div>`;
  if(i===2) return `<p class="muted" style="font-size:13px;margin-bottom:14px">Tech tree — drag to set prerequisites.</p>
    ${['Town Hall → unlocks Workers','Barracks → unlocks Striders [need: Town Hall]','Stables → unlocks War Wagon [need: Barracks]','Spire → unlocks Sky Serpent [need: Stables]'].map(t=>`<div class="eca-item"><span class="pill">TECH</span>${t.replace(/\[(.*?)\]/,'<span class="tag tag--lock" style="margin-left:6px">$1</span>')}</div>`).join('')}`;
  if(i===3) return `<div class="field" style="margin-bottom:14px;max-width:300px"><label>Starting Ore</label><div class="field-slider"><input type="range" class="slider" min="0" max="1000" value="400"><input class="num-input" value="400"></div></div>
    <div class="field" style="margin-bottom:14px;max-width:300px"><label>Starting Workers</label><div class="field-slider"><input type="range" class="slider" min="0" max="12" value="5"><input class="num-input" value="5"></div></div>
    <div class="field" style="max-width:300px"><label>Start Building</label><select class="select"><option>Town Hall (placed)</option><option>Worker only</option></select></div>`;
  return `<p class="muted" style="font-size:13px;margin-bottom:14px">Pick how the AI plays this faction.</p>
    ${[['Rusher','Early aggression, constant pressure'],['Economic','Expands fast, late-game army'],['Balanced','Adapts to scouting'],['Turtle','Defensive, tech-focused']].map((a,k)=>`
    <div class="list-row${k===0?' is-selected':''}" style="margin-bottom:8px"><div class="grow"><div class="hi" style="font-size:13px">${a[0]}</div><div class="muted" style="font-size:11px">${a[1]}</div></div>${k===0?'<span class="valid-badge">Selected</span>':''}</div>`).join('')}`;
}
function wizNav(d){ wcur=Math.max(0,Math.min(4,wcur+d)); renderWiz();
  document.querySelectorAll('#pv-faction .slider').forEach(s=>{const n=s.parentElement.querySelector('.num-input');if(n)s.oninput=()=>n.value=s.value;});}
renderWiz();
