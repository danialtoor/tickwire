#!/usr/bin/env python3
"""Smoke test over raw TCP FIX: provision, logon, NewOrderSingle, expect an ExecutionReport, cancel, logout.

Exit code 0 on success. Used by scripts/smoke.sh against local and production deployments.
"""
import argparse
import sys
import time

from tickwire_client import FixClient, api_get, provision


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--api", required=True)
    p.add_argument("--fix", required=True, help="host:port")
    p.add_argument("--quiet", action="store_true")
    args = p.parse_args()
    host, port = args.fix.rsplit(":", 1)

    creds = provision(args.api)
    chain = api_get(args.api, "/api/chain/SPY")
    row = min(chain["rows"], key=lambda r: abs(r["strike"] - chain["spot"]))
    bid = row["call"]["bid"] or 0.05
    c = FixClient(host, int(port), creds["senderCompID"], creds["targetCompID"], verbose=not args.quiet)
    started = time.perf_counter()
    try:
        c.connect()
        c.logon()
        c.new_order("smoke-1", "SPY", 1, row["strike"], chain["expiry"].replace("-", ""), "1", 1, round(max(bid - 0.05, 0.01), 2))
        ack = c.wait(lambda m: m.msg_type == "8" and m.get(150) != "A")
        if ack.get(39) not in ("0", "1", "2"):
            print(f"FAIL: unexpected OrdStatus {ack.get(39)}: {ack.get(58)}", file=sys.stderr)
            return 1
        if ack.get(39) == "0":
            c.cancel("smoke-1-c", "smoke-1", "SPY", "1", 1)
            c.wait(lambda m: (m.msg_type == "8" and m.get(150) == "4") or m.msg_type == "9")
        c.logout()
    except Exception as e:  # noqa: BLE001 - any failure fails the smoke test
        print(f"FAIL: {type(e).__name__}: {e}", file=sys.stderr)
        return 1
    finally:
        c.close()
    print(f"OK: FIX logon, order, ack, cancel and logout in {time.perf_counter() - started:.2f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
