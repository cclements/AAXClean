#!/usr/bin/env python3
"""Reject missing, empty, skipped, failed, or wrong-scope hermetic test receipts."""

import argparse
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def verify(path: Path, required_classes: list[str]) -> int:
    root = ET.parse(path).getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    summary = root.find("t:ResultSummary", ns)
    counters = root.find("t:ResultSummary/t:Counters", ns)
    if summary is None or counters is None or summary.get("outcome") != "Completed":
        raise ValueError("The test run did not complete.")
    results = root.findall("t:Results/t:UnitTestResult", ns)
    total = int(counters.get("total", "0"))
    executed = int(counters.get("executed", "0"))
    passed = int(counters.get("passed", "0"))
    if not results or not (len(results) == total == executed == passed):
        raise ValueError("The hermetic run must execute and pass every selected test.")
    if any(result.get("outcome") != "Passed" for result in results):
        raise ValueError("A selected test was not passed.")
    classes_by_id = {}
    for test in root.findall("t:TestDefinitions/t:UnitTest", ns):
        method = test.find("t:TestMethod", ns)
        if method is not None:
            classes_by_id[test.get("id")] = method.get("className", "").split(",", 1)[0]
    observed = {classes_by_id.get(result.get("testId")) for result in results}
    missing = set(required_classes) - observed
    if missing:
        raise ValueError("Required regression classes were not executed: " + ", ".join(sorted(missing)))
    return passed


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trx", type=Path)
    parser.add_argument("--require-class", action="append", default=[])
    args = parser.parse_args()
    try:
        count = verify(args.trx, args.require_class)
    except (OSError, ET.ParseError, ValueError) as error:
        print(f"Invalid hermetic test receipt: {error}", file=sys.stderr)
        sys.exit(1)
    print(f"Verified {count} passed hermetic tests and {len(args.require_class)} required regression classes.")
