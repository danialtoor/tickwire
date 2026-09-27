#!/usr/bin/env python3
"""Connect your own FIX client to Tickwire: log on, buy one at-the-money SPY call, cancel what's left, log out.

    pip install -r requirements.txt
    python example.py --api https://tickwire-api.fly.dev                     # provisions credentials for you
    python example.py --host tickwire-api.fly.dev --sender BYO-XXXX           # or use credentials from the Connect page
"""
import argparse

from tickwire_client import FixClient, api_get, provision


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--api", default="http://localhost:8080", help="REST base URL (for the chain and for provisioning)")
    p.add_argument("--host", help="FIX host (default: from provisioning)")
    p.add_argument("--port", type=int, default=9878)
    p.add_argument("--sender", help="your SenderCompID (default: provision a new one)")
    p.add_argument("--target", default="TICKWIRE")
    args = p.parse_args()

    if args.sender is None:
        creds = provision(args.api)
        args.sender, args.target = creds["senderCompID"], creds["targetCompID"]
        args.host = args.host or creds["host"]
        args.port = creds["port"]
        print(f"Provisioned {args.sender} -> {args.target} at {args.host}:{args.port}")

    chain = api_get(args.api, "/api/chain/SPY")
    row = min(chain["rows"], key=lambda r: abs(r["strike"] - chain["spot"]))
    call = row["call"]
    maturity = chain["expiry"].replace("-", "")
    print(f"ATM call: SPY {chain['expiry']} {row['strike']} C, bid {call['bid']} ask {call['ask']} theo {call['theo']}")

    c = FixClient(args.host, args.port, args.sender, args.target)
    c.connect()
    c.logon()
    # Bid a little below the ask so part of the order rests and can be canceled.
    c.new_order("py-1", "SPY", 1, row["strike"], maturity, "1", 5, round(call["bid"], 2))
    ack = c.wait(lambda m: m.msg_type == "8" and m.get(150) in ("0", "8"))
    print(f"ExecType {ack.get(150)} OrdStatus {ack.get(39)} OrderID {ack.get(37)} {ack.get(58) or ''}")
    if ack.get(150) == "0":
        c.cancel("py-1-c", "py-1", "SPY", "1", 5)
        done = c.wait(lambda m: m.msg_type in ("8", "9") and m.get(39) in ("4", "2") or m.msg_type == "9")
        print(f"Cancel result: {done.msg_type} OrdStatus {done.get(39)} {done.get(58) or ''}")
    c.logout()
    c.close()


if __name__ == "__main__":
    main()
