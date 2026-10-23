"""Read-only dump of the SOLIDWORKS type libraries installed on this machine, for looking up API signatures.

No SOLIDWORKS process is started. The dumps are Dassault material: they land in artifacts/typelib/ (ignored by git)
and must never be committed or published.

Usage:
    python -I tools/dump_typelib.py [--install "C:/Program Files/SOLIDWORKS Corp/SOLIDWORKS"] [--out artifacts/typelib]

Writes one line per member, `Interface.Member | kind | dispid | nparams | (Name:TYPE, ...) -> RET`, and one line per
enum constant, `ENUM EnumName.Const = value`. Needs pywin32 (`pip install pywin32`).
"""
import argparse
import os
import sys

import pythoncom

VT_NAMES = {
    0: "EMPTY", 1: "NULL", 2: "I2", 3: "I4", 4: "R4", 5: "R8", 6: "CY", 7: "DATE",
    8: "BSTR", 9: "DISPATCH", 10: "ERROR", 11: "BOOL", 12: "VARIANT", 13: "UNKNOWN",
    16: "I1", 17: "UI1", 18: "UI2", 19: "UI4", 20: "I8", 21: "UI8", 22: "INT", 23: "UINT",
    24: "VOID", 25: "HRESULT", 26: "PTR", 27: "SAFEARRAY", 28: "CARRAY", 29: "USERDEFINED",
}
INVKIND = {1: "method", 2: "propget", 4: "propput", 8: "propputref"}
PARAMFLAG_FOUT, PARAMFLAG_FRETVAL, PARAMFLAG_FOPT = 2, 8, 16

# (type library relative to the install folder, output file name)
LIBRARIES = [
    ("sldworks.tlb", "sldworks_api.txt"),
    ("swconst.tlb", "swconst_enums.txt"),
    ("swcommands.tlb", "swcommands.txt"),
    ("Simulation/cosworks.tlb", "simulation_api.txt"),
]


def type_name(ti, td):
    """td is a TYPEDESC as pythoncom returns it: an int, or a tuple (vt, inner)."""
    if isinstance(td, int):
        return VT_NAMES.get(td, str(td))
    vt = td[0]
    if vt == 26:
        return type_name(ti, td[1]) + "*"
    if vt == 27:
        return "SAFEARRAY(" + type_name(ti, td[1]) + ")"
    if vt == 29:
        try:
            return ti.GetRefTypeInfo(td[1]).GetDocumentation(-1)[0]
        except pythoncom.com_error:
            return "USERDEFINED"
    return VT_NAMES.get(vt, str(vt))


def dump(path):
    tlb = pythoncom.LoadTypeLib(path)
    lines = []
    for i in range(tlb.GetTypeInfoCount()):
        ti = tlb.GetTypeInfo(i)
        name = tlb.GetDocumentation(i)[0]
        attr = ti.GetTypeAttr()
        if attr.typekind == pythoncom.TKIND_ENUM:
            for v in range(attr.cVars):
                vd = ti.GetVarDesc(v)
                lines.append(f"ENUM {name}.{ti.GetNames(vd.memid)[0]} = {vd.value}")
            continue
        if attr.typekind not in (pythoncom.TKIND_INTERFACE, pythoncom.TKIND_DISPATCH):
            continue
        for f in range(attr.cFuncs):
            fd = ti.GetFuncDesc(f)
            names = ti.GetNames(fd.memid)
            params = []
            for j, (td, flags, _default) in enumerate(fd.args):
                pname = names[j + 1] if j + 1 < len(names) else f"p{j}"
                marks = [m for bit, m in ((PARAMFLAG_FOUT, "out"), (PARAMFLAG_FRETVAL, "retval"), (PARAMFLAG_FOPT, "opt"))
                         if flags & bit]
                params.append(f"{pname}:{type_name(ti, td)}" + (f"[{','.join(marks)}]" if marks else ""))
            ret = type_name(ti, fd.rettype[0] if isinstance(fd.rettype, tuple) else fd.rettype)
            lines.append(f"{name}.{names[0]} | {INVKIND.get(fd.invkind, fd.invkind)} | dispid={fd.memid} | "
                         f"nparams={len(fd.args)} optcount={fd.cParamsOpt} | ({', '.join(params)}) -> {ret}")
    return lines


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--install", default=r"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "artifacts", "typelib"))
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    for lib, out_name in LIBRARIES:
        src = os.path.join(args.install, lib)
        if not os.path.isfile(src):
            print(f"skip {lib}: not found under {args.install}", file=sys.stderr)
            continue
        lines = dump(src)
        dest = os.path.join(args.out, out_name)
        with open(dest, "w", encoding="utf-8") as fh:
            fh.write("\n".join(lines) + "\n")
        print(f"{lib}: {len(lines)} lines -> {os.path.normpath(dest)}")


if __name__ == "__main__":
    main()
