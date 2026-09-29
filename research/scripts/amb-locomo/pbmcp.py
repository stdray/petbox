import os, json, httpx
"""Minimal PetBox MCP client (streamable HTTP, JSON-RPC tools/call). Key comes from the caller."""
URL=os.environ.get("PETBOX_MCP_URL","https://petbox.3po.su/mcp")
class MCP:
    def __init__(self, key):
        self.c=httpx.Client(timeout=600, headers={"X-Api-Key":key,"Accept":"application/json, text/event-stream","Content-Type":"application/json"})
        self.sid=None; self.i=0
        r=self._post({"jsonrpc":"2.0","id":self._id(),"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"amb-petbox","version":"0"}}})
        self._post({"jsonrpc":"2.0","method":"notifications/initialized"},notify=True)
    def _id(self): self.i+=1; return self.i
    def _post(self,body,notify=False):
        h={}
        if self.sid: h["Mcp-Session-Id"]=self.sid
        r=self.c.post(URL,json=body,headers=h)
        if "mcp-session-id" in r.headers: self.sid=r.headers["mcp-session-id"]
        if notify: return None
        r.raise_for_status()
        t=r.text
        if r.headers.get("content-type","").startswith("text/event-stream"):
            for line in t.splitlines():
                if line.startswith("data:"): return json.loads(line[5:])
        return r.json()
    def call(self,name,args):
        r=self._post({"jsonrpc":"2.0","id":self._id(),"method":"tools/call","params":{"name":name,"arguments":args}})
        if "error" in r: raise RuntimeError(r["error"])
        res=r["result"]
        txt="".join(p.get("text","") for p in res.get("content",[]))
        if res.get("isError"): raise RuntimeError(txt[:500])
        try: return json.loads(txt)
        except Exception: return txt
