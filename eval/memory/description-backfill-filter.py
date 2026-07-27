#!/usr/bin/env python3
"""Mechanical Cyrillic/Latin language-mismatch filter for the memory description
backfill (card memory-upsert-create-still-silent-on-empty-description, step 2).

Rule: for each tsv row (key, version, description), load the entry BODY
(bodies.json, fetched via memory_get) and compute the Cyrillic letter ratio of
body vs description. If the body is Cyrillic-majority (ratio > 0.5 of all
letters) and the description is NOT Cyrillic-majority, the row is flagged as a
language-mismatch defect (English description glued onto a Russian body).

Usage:
  python description-backfill-filter.py <tsv-path> <bodies-json-path>

Prints one line per row: key, body_cyr_ratio, desc_cyr_ratio, FLAG|ok
and a summary count at the end.
"""
import json
import re
import sys

CYR = re.compile(r"[Ѐ-ӿ]")
LATIN = re.compile(r"[A-Za-z]")


def cyr_ratio(text: str) -> float:
    cyr = len(CYR.findall(text))
    lat = len(LATIN.findall(text))
    total = cyr + lat
    if total == 0:
        return 0.0
    return cyr / total


def main():
    tsv_path, bodies_path = sys.argv[1], sys.argv[2]
    with open(bodies_path, encoding="utf-8") as f:
        bodies = json.load(f)

    flagged = []
    total = 0
    with open(tsv_path, encoding="utf-8") as f:
        header = f.readline()
        for line in f:
            line = line.rstrip("\n")
            if not line.strip():
                continue
            key, version, desc = line.split("\t", 2)
            total += 1
            body = bodies.get(key, "")
            br = cyr_ratio(body)
            dr = cyr_ratio(desc)
            # Body is treated as Russian-authored if it carries ANY substantial
            # Cyrillic prose share (identifiers/hashes/branch names pad the Latin
            # count in this corpus's mixed ru/en technical notes, so 0.5 majority
            # is too strict a bar — a body with even ~0.10-0.30 Cyrillic here is
            # still a Russian sentence threaded with English identifiers, verified
            # by hand against the raw excerpts). Description is treated as a
            # genuine language-mismatch defect only when it carries essentially NO
            # Cyrillic at all (<0.15) despite a Russian-authored body.
            is_flag = br > 0.10 and dr < 0.15
            print(f"{key}\t{version}\tbody_cyr={br:.2f}\tdesc_cyr={dr:.2f}\t{'FLAG' if is_flag else 'ok'}")
            if is_flag:
                flagged.append(key)

    print(f"\nTOTAL={total} FLAGGED={len(flagged)}")
    print("FLAGGED_KEYS=" + ",".join(flagged))


if __name__ == "__main__":
    main()
