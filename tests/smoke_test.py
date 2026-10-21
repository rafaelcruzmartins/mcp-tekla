"""Teste de fumaça do TeklaMcp via stdio (JSON-RPC cru, sem dependências).

Uso:  python -I tests\\smoke_test.py [caminho\\TeklaMcp.exe] [--write]
Com --write cria objetos MCP_TESTE_* no modelo aberto (use um modelo de teste).
"""
import json
import os
import subprocess
import sys

args = [a for a in sys.argv[1:] if not a.startswith("--")]
WRITE = "--write" in sys.argv
EXE = args[0] if args else os.path.join(os.path.dirname(__file__), "..", "bin", "Release", "net48", "TeklaMcp.exe")

proc = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
_next = 0


def rpc(method, params=None, notify=False):
    global _next
    msg = {"jsonrpc": "2.0", "method": method}
    if params is not None:
        msg["params"] = params
    if not notify:
        _next += 1
        msg["id"] = _next
    proc.stdin.write((json.dumps(msg) + "\n").encode("utf-8"))
    proc.stdin.flush()
    if notify:
        return None
    while True:
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError("servidor encerrou")
        resp = json.loads(line)
        if resp.get("id") == msg["id"]:
            return resp


failures = []


def call(_tool, **arguments):
    r = rpc("tools/call", {"name": _tool, "arguments": arguments})
    res = r.get("result", {})
    texts = [c.get("text", "") for c in res.get("content", []) if c.get("type") == "text"]
    kinds = [c.get("type") for c in res.get("content", [])]
    payload = json.loads(texts[0]) if texts else {}
    ok = not res.get("isError") and payload.get("ok")
    short = json.dumps(payload.get("result") if ok else payload.get("error"), ensure_ascii=False)
    print(f"[{'OK ' if ok else 'ERR'}] {_tool}: {short[:300]}{' +' + ','.join(kinds[1:]) if len(kinds) > 1 else ''}")
    if not ok:
        failures.append(_tool)
    return payload.get("result") if ok else None


init = rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}})
print("server:", init["result"]["serverInfo"])
rpc("notifications/initialized", notify=True)
tools = rpc("tools/list")["result"]["tools"]
print(f"{len(tools)} ferramentas:", ", ".join(t["name"] for t in tools))

call("get_tekla_status")
call("get_project_info")
call("list_phases")
call("list_profiles", contains="W310", limit=5)
call("list_materials", contains="A572", limit=5)
call("list_bolts", standardContains="A325")
call("list_components", contains="144", limit=5)
call("list_views")

if WRITE:
    prof = (call("list_profiles", contains="W310", limit=1) or {}).get("profiles", [{}])
    prof = prof[0].get("name", "HEA200") if prof else "HEA200"
    mat = (call("list_materials", contains="A572", limit=1) or [{}])
    mat = mat[0].get("name") if mat else None
    col = call("create_column", basePoint=[0, 0, 0], profile=prof, material=mat, height=4000, name="MCP_TESTE_PILAR")
    col2 = call("create_column", basePoint=[6000, 0, 0], profile=prof, material=mat, height=4000, name="MCP_TESTE_PILAR")
    beam = call("create_beam", start=[0, 0, 4000], end=[6000, 0, 4000], profile=prof, material=mat, name="MCP_TESTE_VIGA",
                rotation="TOP", depth="BEHIND", userProperties={"comment": "criado pelo MCP"})
    plate = call("create_contour_plate", points=[[0, 1000, 0], [2000, 1000, 0], [2000, 2000, 0], [0, 2000, 0]],
                 profile="PL20", material=mat, name="MCP_TESTE_CHAPA")
    if col and beam:
        call("get_object_properties", id=beam["id"])
        call("set_user_properties", ids=[beam["id"]], properties={"comment": "alterado"})
        call("modify_part", id=beam["id"], name="MCP_TESTE_VIGA2")
        call("create_weld", mainPart=col["id"], secondaryPart=beam["id"], sizeAbove=6)
        call("insert_component", kind="connection", number=144, primaryId=col2["id"], secondaryIds=[beam["id"]])
        cp = call("copy_objects", ids=[col["id"]], vector=[0, 6000, 0], copies=2)
        call("move_objects", ids=[plate["id"]], vector=[0, 0, -100])
        call("get_report_properties", ids=[col["id"], beam["id"]], properties=["WEIGHT", "LENGTH", "PROFILE", "MATERIAL"])
        call("color_objects", ids=[beam["id"]], rgb=[1, 0, 0])
        call("select_objects", ids=[beam["id"]])
        call("get_selected_objects")
        call("zoom_to_objects", ids=[col["id"], beam["id"]])
        call("capture_view")
        call("clear_colors")
        call("execute_csharp", code="var b = (Beam)Get(%d); Print(b.Profile.ProfileString); return All(typeof(Beam)).Count;" % beam["id"], commit=False)
        call("execute_csharp", code="int x = ;")  # erro de compilação esperado
        call("material_takeoff")
        call("list_objects", nameContains="MCP_TESTE", reportProperties=["WEIGHT"])
        call("check_numbering")
    call("create_grid", coordinateX="0 2*6000", coordinateY="0 6000", coordinateZ="0 4000", labelX="1 2 3", labelY="A B", labelZ="+0 +4000")
    call("list_objects", types=["Grid", "Connection", "Weld"])

if "--extra" in sys.argv:
    import tempfile
    out = os.path.join(tempfile.gettempdir(), "teklamcp_test")
    prof = "W310X23.8"
    a = call("create_column", basePoint=[20000, 0, 0], profile=prof, height=3000, name="MCP_TESTE_EXTRA")
    p = call("create_contour_plate", points=[[19850, -100, 3000], [20150, -100, 3000], [20150, 100, 3000], [19850, 100, 3000]],
             profile="PL16", name="MCP_TESTE_EXTRA")
    if a and p:
        call("create_bolt_group", partToBoltTo=a["id"], partToBeBolted=p["id"], firstPoint=[19900, -60, 3000],
             secondPoint=[20100, -60, 3000], distX=[0, 200], distY=[120], boltSize=19.05, boltStandard="A325N",
             rotation="TOP")
        call("get_object_properties", id=p["id"])
    pb = call("create_polybeam", points=[[20000, 2000, 0], [22000, 2000, 0], [22000, 4000, 0]], profile=prof, name="MCP_TESTE_EXTRA")
    b = call("create_beam", start=[24000, 0, 0], end=[30000, 0, 0], profile=prof, name="MCP_TESTE_EXTRA")
    if b:
        call("split_part", id=b["id"], point=[27000, 0, 0])
    conn = call("list_objects", types=["Connection"], limit=1)
    if conn and conn["objects"]:
        call("get_component_attributes", id=conn["objects"][0]["id"], names=["tpl1", "mat", "nb", "bolt_standard"])
    call("list_user_properties", contains="COMMENT", limit=5)
    call("set_visibility", mode="TRANSPARENT")
    call("set_visibility", mode="VISIBLE")
    call("run_macro", code='''namespace Tekla.Technology.Akit.UserScript
{
    public class Script
    {
        public static void Run(Tekla.Technology.Akit.IScript akit)
        {
            akit.Callback("acmd_full_numbering", "", "main_frame");
        }
    }
}''')
    call("check_numbering")
    if a:
        call("create_drawing", kind="assembly", objectId=a["id"])
        call("create_drawing", kind="single_part", objectId=a["id"])
    call("create_drawing", kind="ga", name="MCP_TESTE_GA")
    dw = call("list_drawings")
    if dw and dw["drawings"]:
        call("get_drawing_objects", mark=dw["drawings"][0]["mark"])
        call("export_drawings_pdf", outputFolder=out, marks=[dw["drawings"][0]["mark"]])
    call("export_ifc", filePath=os.path.join(out, "teste.ifc"))
    call("create_report", template="Material_List", outputFile=os.path.join(out, "material.xsr"), returnChars=1500)
    call("save_model", comment="teste MCP")

call("list_drawings")
proc.stdin.close()
proc.wait(timeout=10)
print("\nFALHAS:", failures if failures else "nenhuma (o erro de compilação proposital conta como falha esperada)")
