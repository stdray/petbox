import os, sys, json, time, re, threading, argparse
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.join(os.environ.get("AMB_DIR","amb"),"src"))
from pbmcp import MCP
from memory_bench.dataset.locomo import LoComoDataset
from memory_bench.dataset.base import _DEFAULT_OPEN_PROMPT

ap=argparse.ArgumentParser()
ap.add_argument("--convs",default="all"); ap.add_argument("--k",type=int,default=25)
ap.add_argument("--win",type=int,default=6); ap.add_argument("--par",type=int,default=8)
ap.add_argument("--out",default="res_locomo.json"); ap.add_argument("--keep",action="store_true")
ap.add_argument("--qlimit",type=int,default=0)
a=ap.parse_args()

# HARD RULE (AGENTS.md rule 7): every write goes to project `smoke` with a sandboxOnly key.
# PETBOX_SMOKE_API_KEY: sandboxOnly key for `smoke` (memory:read/write).
# PETBOX_LLM_API_KEY: sandboxOnly `smoke` key with llm:invoke (answer + judge via llm_chat).
SMOKE=os.environ["PETBOX_SMOKE_API_KEY"]; LLMK=os.environ["PETBOX_LLM_API_KEY"]
PROJ="smoke"
tl=threading.local()
for _k in (SMOKE, LLMK):
    _w=MCP(_k).call("whoami",{})
    assert _w["project"]=="smoke", f"refusing to run: key is bound to {_w['project']!r}, not 'smoke'"
def mcp(key):
    d=getattr(tl,"d",None)
    if d is None: d=tl.d={}
    if key not in d: d[key]=MCP(key)
    return d[key]
stats={"llm_calls":0,"search_calls":0,"upsert_calls":0}; lock=threading.Lock()
def bump(k):
    with lock: stats[k]+=1

def llm_json(prompt, props):
    ex=", ".join(f'"{k}": <{v}>' for k,v in props.items())
    p=prompt+f"\n\nRespond with ONLY a single JSON object, no markdown fences: {{{ex}}}"
    for att in range(5):
        try:
            bump("llm_calls")
            r=mcp(LLMK).call("llm_chat",{"projectKey":PROJ,"messages":[{"role":"user","content":p}],"temperature":0})
            t=r["text"].strip()
            t=re.sub(r"^```(?:json)?|```$","",t,flags=re.M).strip()
            m=re.search(r"\{.*\}",t,re.S)
            return json.loads(m.group(0)), r["servedBy"]["upstreamModel"]
        except Exception as e:
            err=str(e)
            time.sleep(3*(att+1))
    raise RuntimeError("llm failed: "+err[:200])

ds=LoComoDataset()
queries=ds.load_queries("locomo10")
raw=ds._load_raw()
convs=[r["sample_id"] for r in raw] if a.convs=="all" else a.convs.split(",")
if a.qlimit: 
    queries=[q for q in queries if q.user_id in convs]
    per={}; 
    sel=[]
    for q in queries:
        per[q.user_id]=per.get(q.user_id,0)+1
        if per[q.user_id]<=a.qlimit: sel.append(q)
    queries=sel
queries=[q for q in queries if q.user_id in convs]

def store_of(c): return "ambloc"+re.sub(r"\W","",c)
def turn_line(t):
    s=f"{t['speaker']}: {t['text']}"
    if t.get("blip_caption"): s+=f" [shares an image: {t['blip_caption']}]"
    return s

def ingest(item):
    c=item["sample_id"]; conv=item["conversation"]; st=store_of(c)
    m=mcp(SMOKE)
    try: m.call("memory_store_create",{"projectKey":PROJ,"store":st,"description":"AMB LoCoMo benchmark run (temporary)"})
    except Exception as e: pass
    entries=[]
    for sk in ds._session_keys(conv):
        date=conv.get(sk+"_date_time","")
        turns=conv[sk]
        for i in range(0,len(turns),a.win):
            lines=[turn_line(t) for t in turns[i:i+a.win]]
            head=f"Conversation between {conv['speaker_a']} and {conv['speaker_b']}, {sk.replace('_',' ')}, {date}"
            body=head+chr(10)+chr(10).join(lines)
            entries.append({"key":f"{sk}-c{i//a.win:02d}","type":"Reference","description":(head+" | "+lines[0])[:155],"body":body})
    def existing():
        keys=set(); cur=None
        while True:
            args={"projectKey":PROJ,"store":st,"scope":"project","limit":len(entries)+5,"bodyLen":0}
            if cur: args["cursor"]=cur
            r=m.call("memory_search",args)
            keys|={x["key"] for x in r.get("items",[])}
            cur=r.get("nextCursor")
            if not cur: return keys
    have=existing()
    todo=[e for e in entries if e["key"] not in have]
    n=0
    for i in range(0,len(todo),10):
        for att in range(6):
            try:
                r=m.call("memory_upsert",{"projectKey":PROJ,"store":st,"entries":todo[i:i+10]}); bump("upsert_calls")
                if not r.get("applied"): raise RuntimeError(str(r)[:400])
                n+=r.get("inserted",0); break
            except Exception as e:
                print("ingest retry",c,i,str(e)[:100],flush=True)
                tl.d.pop(SMOKE,None); m=mcp(SMOKE); time.sleep(10)
                have=existing(); 
                todo2=[e for e in todo[i:i+10] if e["key"] not in have]
                if not todo2: break
                todo[i:i+10]=todo2
        else: raise RuntimeError("ingest failed "+c)
    have=existing()
    assert len(have)>=len(entries), (c,len(have),len(entries))
    return c,len(entries),n

t0=time.time()
items=[r for r in raw if r["sample_id"] in convs]
with ThreadPoolExecutor(4) as ex: ing=list(ex.map(ingest,items))
ing_s=time.time()-t0
print("ingested",ing,f"{ing_s:.0f}s",flush=True)
# wait for embeddings
for c,_,_ in ing:
    for _ in range(120):
        r=mcp(SMOKE).call("memory_search",{"projectKey":PROJ,"store":store_of(c),"q":"test","limit":1,"scope":"project","bodyLen":0})
        rt=r.get("retrievers") or {}
        if rt.get("semantic") and not rt.get("semanticLag") and not rt.get("degraded"): break
        time.sleep(5)
    print(c,"retrievers",rt,flush=True)

ANS={"reasoning":"string","answer":"string, the final concise answer"}
def do(q):
    t=time.time()
    r=mcp(SMOKE).call("memory_search",{"projectKey":PROJ,"store":store_of(q.user_id),"q":q.query,"limit":a.k,"scope":"project","bodyLen":-1}); bump("search_calls")
    ret_ms=(time.time()-t)*1000
    items=r.get("items",[])
    ctx="\n\n".join(f"## Memory {i+1}\n{x.get('body') or x['description']}" for i,x in enumerate(items))
    prompt=ds.build_rag_prompt(q.query,ctx,"open","locomo10",None,q.meta)
    ans,model=llm_json(prompt,ANS)
    jp=ds.build_judge_prompt(q.query,q.gold_answers,ans["answer"])
    j,_=llm_json(jp,{"reason":"string, one sentence","correct":"boolean"})
    ok=j.get("correct") in (True,"true","True")
    gold_hit=None
    return {"id":q.id,"cat":q.meta["category"],"conv":q.user_id,"q":q.query,"gold":q.gold_answers[0],"ans":ans["answer"],"correct":ok,"why":j.get("reason"),"n_ret":len(items),"ctx_chars":len(ctx),"ret_ms":ret_ms,"retrievers":r.get("retrievers"),"top_keys":[x["key"] for x in items],"gold_sessions":q.gold_ids,"model":model}
t1=time.time()
done=[0]
def wrap(q):
    try: res=do(q)
    except Exception as e: res={"id":q.id,"cat":q.meta["category"],"conv":q.user_id,"error":str(e)[:300],"correct":False}
    done[0]+=1
    if done[0]%50==0: print(done[0],"/",len(queries),f"{time.time()-t1:.0f}s",flush=True)
    return res
with ThreadPoolExecutor(a.par) as ex: res=list(ex.map(wrap,queries))
qs=time.time()-t1
errs=[r for r in res if "error" in r]
from collections import defaultdict
by=defaultdict(lambda:[0,0])
for r in res:
    by[r["cat"]][1]+=1; by[r["cat"]][0]+=r["correct"]
tot=sum(r["correct"] for r in res)
summ={"n":len(res),"correct":tot,"acc":tot/len(res),"errors":len(errs),"by_cat":{k:(v[0],v[1],v[0]/v[1]) for k,v in by.items()},"ingest_s":ing_s,"query_s":qs,"stats":stats,"k":a.k,"win":a.win,"convs":convs}
json.dump({"summary":summ,"results":res},open(a.out,"w"),indent=1)
print(json.dumps(summ,indent=1))
if not a.keep:
    for c,_,_ in ing:
        try: mcp(SMOKE).call("memory_store_delete",{"projectKey":PROJ,"store":store_of(c)})
        except Exception as e: print("cleanup fail",c,e)
