"""Independent ERP v3 reader used by the tests (shares no code with EgoEngineLibrary).

usage: second_reader.py <out.serps> <template.serps> <private_token>[,<private_token>...] <entry> [<entry> ...]
       an <entry> is a zip path, or "<path in out.serps>|<path in template.serps>" when the build renamed its folder

For every listed zip entry: parses the ERPK table of contents, decompresses every fragment (none / zlib / zstd)
and checks the decompressed sizes; checks the MD5 hash of every resource that is new or changed compared with
the same entry in the template (SERPs-written resources often carry hashes the game ignores); and checks that
every eaid:// reference into the private model namespaces resolves to a resource in the same ERP. Prints one JSON line; exit code 1 on failure.
"""
import hashlib
import json
import re
import struct
import sys
import zipfile
import zlib

import zstandard

EAID = re.compile(rb"eaid://[\x21-\x7e]+?(?=\x00|$)")


def read_erp(d):
    if d[:4] != b"ERPK":
        raise ValueError("not an ERPK file")
    ver = struct.unpack_from("<I", d, 4)[0]
    if ver != 3:
        raise ValueError(f"ERP version {ver}")
    toc_off, toc_size, data_off = struct.unpack_from("<QQQ", d, 0x10)
    p = toc_off
    nres, nfrag = struct.unpack_from("<II", d, p)
    p += 8
    out, total = [], 0
    for _ in range(nres):
        start = p
        esize, nlen = struct.unpack_from("<IH", d, p)
        p += 6
        name = d[p:p + nlen].rstrip(b"\0").decode("ascii")
        p += nlen
        rtype = d[p:p + 16].rstrip(b"\0").decode("ascii")
        p += 21
        nf = d[p - 1]
        frags = []
        for _ in range(nf):
            off, size, flags, comp, packed = struct.unpack_from("<QQIBQ", d, p + 4)
            p += 33
            frags.append((off, size, comp, packed))
            total += 1
        h = d[p:p + 16]
        p += 16
        if p - start != esize + 4:
            raise ValueError(f"{name}: TOC entry size mismatch")
        out.append((name, rtype, frags, h, d[toc_off:p]))
    if total != nfrag:
        raise ValueError(f"fragment count {total} != header {nfrag}")
    if p != toc_off + toc_size:
        raise ValueError("TOC size mismatch")
    return out, data_off


def main():
    serps, template, tokens, names = sys.argv[1], sys.argv[2], [t for t in sys.argv[3].split(",") if t], sys.argv[4:]
    zd = zstandard.ZstdDecompressor()
    report = {"entries": 0, "resources": 0, "fragments": 0, "errors": []}
    with zipfile.ZipFile(serps) as z, zipfile.ZipFile(template) as zt:
        for spec in names:
            n, _, orig = spec.partition("|")
            orig = orig or n
            d = z.read(n)
            res, data_off = read_erp(d)
            report["entries"] += 1
            before = {}
            if orig in zt.namelist():
                td = zt.read(orig)
                tres, toff = read_erp(td)
                before = {r[0]: (r[3], [td[toff + o:toff + o + pk] for o, _, _, pk in r[2]]) for r in tres}
            ids, refs = set(), []
            for name, rtype, frags, h, _ in res:
                ids.add(name)
                blob = bytearray()
                for off, size, comp, packed in frags:
                    raw = d[data_off + off:data_off + off + packed]
                    if len(raw) != packed:
                        report["errors"].append(f"{name}: fragment past end of file")
                        continue
                    if comp in (0x00, 0x90, 0x91):
                        dec = raw
                    elif comp == 0x01:
                        dec = zlib.decompress(raw)
                    elif comp in (0x10, 0x11):
                        dec = zd.decompress(raw, max_output_size=size)
                    else:
                        report["errors"].append(f"{name}: unknown compression {comp:#x}")
                        continue
                    if len(dec) != size:
                        report["errors"].append(f"{name}: fragment is {len(dec)} bytes, TOC says {size}")
                    blob += dec
                    report["fragments"] += 1
                raws = [d[data_off + o:data_off + o + pk] for o, _, _, pk in frags]
                changed = before.get(name) != (h, raws)
                if changed:
                    report["checked_hashes"] = report.get("checked_hashes", 0) + 1
                if changed and hashlib.md5(blob).digest() != h:
                    report["errors"].append(f"{name}: MD5 hash mismatch")
                if any(f"/idf/{t}.idf" in name for t in tokens):
                    refs += [(name, m.group().decode("ascii")) for m in EAID.finditer(bytes(blob))]
                report["resources"] += 1
            for src, ref in refs:
                if any(f"/idf/{t}.idf" in ref for t in tokens) and ref not in ids:
                    report["errors"].append(f"{n}: {src.split('/')[-1]} -> missing {ref.split('/')[-1]}")
    report["errors"] = report["errors"][:20]
    print(json.dumps(report))
    sys.exit(1 if report["errors"] else 0)


if __name__ == "__main__":
    main()
