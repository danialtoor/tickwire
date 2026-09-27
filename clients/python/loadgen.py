#!/usr/bin/env python3
"""Load generator: several FIX sessions sending resting orders and cancels at a target message rate.

Measures client-side NewOrderSingle -> first ExecutionReport latency. Resting orders are placed below the bid and
canceled right after the ack, so the book doesn't fill up.

    python loadgen.py --api http://localhost:8080 --fix localhost:9878 --sessions 4 --rate 1000 --seconds 60
"""
import argparse
import statistics
import threading
import time

import tickwire_client
from tickwire_client import FixClient, api_get, api_put, provision


def worker(args, host, port, rate, results, errors, stop):
    creds = provision(args.api)
    # Load needs higher limits than a guest's default; the guest can raise its own within the sandbox caps.
    api_post_limits(args.api, creds, rate)
    chain = api_get(args.api, "/api/chain/SPY")
    row = min(chain["rows"], key=lambda r: abs(r["strike"] - chain["spot"]))
    px = round(max((row["call"]["bid"] or 1) - 0.25, 0.01), 2)
    maturity = chain["expiry"].replace("-", "")
    c = FixClient(host, port, creds["senderCompID"], creds["targetCompID"], verbose=False)
    c.connect()
    c.logon()
    interval = 2.0 / rate  # each cycle is two messages (new + cancel)
    n = 0
    next_at = time.perf_counter()
    try:
        while not stop.is_set():
            n += 1
            cl = f"lg{n}"
            sent = time.perf_counter()
            c.new_order(cl, "SPY", 1, row["strike"], maturity, "1", 1, px)
            ack = c.wait(lambda m: m.msg_type == "8" and m.get(11) == cl and m.get(150) != "A", timeout=10)
            results.append((time.perf_counter() - sent) * 1e6)
            if ack.get(39) == "0":
                c.cancel(f"{cl}c", cl, "SPY", "1", 1)
            else:
                errors.append(ack.get(58) or ack.get(39))
            c.inbox.clear()
            next_at += interval
            delay = next_at - time.perf_counter()
            if delay > 0:
                time.sleep(delay)
    finally:
        c.logout()
        c.close()


def api_post_limits(api, creds, per_session_rate):
    # Guests may raise their own limits up to 100 msgs/s; with an admin key the cap is lifted.
    cap = 100 if tickwire_client.ADMIN_KEY is None else 100_000
    body = {"maxOrderQty": 100, "maxNotional": 50000, "priceBandPct": 0.5, "priceBandMinAbs": 0.25, "maxOpenOrders": 1000,
            "allowedUnderlyings": None, "allowedOrderTypes": ["Limit", "Market"],
            "allowedTimeInForce": ["Day", "ImmediateOrCancel", "FillOrKill"],
            "maxMessagesPerSecond": min(cap, int(per_session_rate * 1.5) + 10), "cancelOnDisconnect": True}
    api_put(api, f"/api/clients/{creds['clientId']}/limits", creds["token"], body)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--api", default="http://localhost:8080")
    p.add_argument("--fix", default="localhost:9878")
    p.add_argument("--sessions", type=int, default=4)
    p.add_argument("--rate", type=float, default=400, help="total messages/second across sessions")
    p.add_argument("--seconds", type=float, default=30)
    p.add_argument("--admin-key", help="operator key: lifts guest rate limits so more sessions and higher rates are possible")
    args = p.parse_args()
    tickwire_client.ADMIN_KEY = args.admin_key
    host, port = args.fix.rsplit(":", 1)
    per_session = args.rate / args.sessions if args.admin_key else min(args.rate / args.sessions, 60)
    results, errors, stop = [], [], threading.Event()
    threads = [threading.Thread(target=worker, args=(args, host, int(port), per_session, results, errors, stop), daemon=True)
               for _ in range(args.sessions)]
    for t in threads:
        t.start()
    started = time.perf_counter()
    time.sleep(args.seconds)
    stop.set()
    for t in threads:
        t.join(timeout=15)
    elapsed = time.perf_counter() - started
    if not results:
        print("no results")
        return
    s = sorted(results)
    pct = lambda q: s[min(len(s) - 1, int(q * len(s)))]  # noqa: E731
    print(f"{len(results)} orders in {elapsed:.0f}s from {args.sessions} session(s): {2 * len(results) / elapsed:.0f} msgs/s sent")
    print(f"order -> ack latency (client side, incl. network): p50 {pct(0.5):.0f} us, p90 {pct(0.9):.0f} us, p99 {pct(0.99):.0f} us, "
          f"max {s[-1]:.0f} us, mean {statistics.mean(s):.0f} us")
    print(f"rejects/other: {len(errors)}")


if __name__ == "__main__":
    main()
