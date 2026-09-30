#!/usr/bin/env python3
"""Generates the curated "broken" FIX logs in samples/ used by the Log Analyzer demo and its tests.

Each log is valid FIX except for the faults it is meant to demonstrate, so BodyLength and CheckSum are computed here.
Run from the repo root: python3 scripts/gen-sample-logs.py
"""
from __future__ import annotations

import json
from datetime import datetime, timedelta
from pathlib import Path

SOH = "\x01"
OUT = Path(__file__).resolve().parent.parent / "samples"
T0 = datetime(2025, 3, 14, 13, 30, 0)


def ts(seconds: float) -> str:
    t = T0 + timedelta(seconds=seconds)
    return t.strftime("%Y%m%d-%H:%M:%S.") + f"{t.microsecond // 1000:03d}"


def fix(sender: str, target: str, seq: int, msg_type: str, t: float, body: list[tuple[int, str]],
        poss_dup: bool = False, orig_t: float | None = None, bad_checksum: bool = False, bad_length: int = 0) -> str:
    header = [(35, msg_type), (49, sender), (56, target), (34, str(seq))]
    if poss_dup:
        header.append((43, "Y"))
    header.append((52, ts(t)))
    if poss_dup:
        header.append((122, ts(orig_t if orig_t is not None else t)))
    payload = "".join(f"{k}={v}{SOH}" for k, v in header + body)
    head = f"8=FIX.4.4{SOH}9={len(payload) + bad_length}{SOH}"
    checksum = sum((head + payload).encode("ascii")) % 256
    if bad_checksum:
        checksum = (checksum + 7) % 256
    return f"{head}{payload}10={checksum:03d}{SOH}"


def nos(cl: str, side: str = "1", qty: int = 10, px: str = "4.25", strike: str = "560", symbol: str | None = "SPY") -> list[tuple[int, str]]:
    body = [(11, cl), (21, "1")]
    if symbol:
        body.append((55, symbol))
    body += [(167, "OPT"), (541, "20250620"), (201, "1"), (202, strike), (54, side), (60, ts(0)), (38, str(qty)), (40, "2"), (44, px), (59, "0")]
    return body


def er(order_id: str, cl: str, exec_id: str, exec_type: str, status: str, qty: int, cum: int, leaves: int,
       last_qty: int = 0, last_px: str = "0", avg: str = "0", text: str | None = None, orig: str | None = None) -> list[tuple[int, str]]:
    body = [(37, order_id), (11, cl)]
    if orig:
        body.append((41, orig))
    body += [(17, exec_id), (150, exec_type), (39, status), (55, "SPY"), (167, "OPT"), (541, "20250620"), (201, "1"), (202, "560"),
             (54, "1"), (38, str(qty))]
    if last_qty:
        body += [(32, str(last_qty)), (31, last_px)]
    body += [(151, str(leaves)), (14, str(cum)), (6, avg), (60, ts(0))]
    if text:
        body.append((58, text))
    return body


def logon(hb: int = 30, reset: bool = False) -> list[tuple[int, str]]:
    return [(98, "0"), (108, str(hb))] + ([(141, "Y")] if reset else [])


def quickfix_line(t: float, direction: str, msg: str) -> str:
    """QuickFIX-style log line with a timestamp and the message '|' delimited, as support usually receives it."""
    return f"{ts(t)} {direction} {msg.replace(SOH, '|')}"


C, V = "CLIENT", "TICKWIRE"


def sample_gap() -> list[str]:
    lines = [
        quickfix_line(0.0, "OUT", fix(C, V, 1, "A", 0.0, logon())),
        quickfix_line(0.1, "IN ", fix(V, C, 1, "A", 0.1, logon())),
        quickfix_line(1.0, "OUT", fix(C, V, 2, "D", 1.0, nos("ORD-1"))),
        quickfix_line(1.1, "IN ", fix(V, C, 2, "8", 1.1, er("TW1", "ORD-1", "E1", "0", "0", 10, 0, 10))),
        quickfix_line(1.2, "IN ", fix(V, C, 3, "8", 1.2, er("TW1", "ORD-1", "E2", "F", "1", 10, 4, 6, 4, "4.25", "4.25"))),
        # 4 (a fill) and 5 (a heartbeat) are lost in transit; 6 arrives.
        quickfix_line(2.5, "IN ", fix(V, C, 6, "8", 2.5, er("TW1", "ORD-1", "E4", "F", "1", 10, 8, 2, 2, "4.26", "4.2525"))),
        quickfix_line(2.5, "OUT", fix(C, V, 3, "2", 2.5, [(7, "4"), (16, "0")])),
        quickfix_line(2.6, "IN ", fix(V, C, 4, "8", 2.6, er("TW1", "ORD-1", "E3", "F", "1", 10, 6, 4, 2, "4.25", "4.25"), poss_dup=True, orig_t=1.9)),
        quickfix_line(2.6, "IN ", fix(V, C, 5, "4", 2.6, [(123, "Y"), (36, "6")], poss_dup=True, orig_t=2.6)),
        quickfix_line(2.6, "IN ", fix(V, C, 6, "8", 2.6, er("TW1", "ORD-1", "E4", "F", "1", 10, 8, 2, 2, "4.26", "4.2525"), poss_dup=True, orig_t=2.5)),
        quickfix_line(3.0, "IN ", fix(V, C, 7, "8", 3.0, er("TW1", "ORD-1", "E5", "F", "2", 10, 10, 0, 2, "4.27", "4.2560"))),
        quickfix_line(3.1, "OUT", fix(C, V, 4, "D", 3.1, nos("ORD-2", qty=5, px="4.10"))),
        quickfix_line(3.2, "IN ", fix(V, C, 8, "8", 3.2, er("TW2", "ORD-2", "E6", "0", "0", 5, 0, 5))),
        # 9 and 10 never arrive and nobody asks for them.
        quickfix_line(9.0, "IN ", fix(V, C, 11, "0", 9.0, [])),
        quickfix_line(9.1, "OUT", fix(C, V, 5, "0", 9.1, [])),
    ]
    return lines


def sample_seq_too_low() -> list[str]:
    return [
        quickfix_line(0.0, "OUT", fix(C, V, 37, "A", 0.0, logon())),
        quickfix_line(0.1, "IN ", fix(V, C, 52, "A", 0.1, logon())),
        quickfix_line(1.0, "OUT", fix(C, V, 38, "D", 1.0, nos("ORD-7"))),
        quickfix_line(1.1, "IN ", fix(V, C, 53, "8", 1.1, er("TW7", "ORD-7", "E70", "0", "0", 10, 0, 10))),
        quickfix_line(5.0, "OUT", fix(C, V, 39, "0", 5.0, [])),
        # The client restarts without its store and logs on from 1 without ResetSeqNumFlag.
        quickfix_line(60.0, "OUT", fix(C, V, 1, "A", 60.0, logon())),
        quickfix_line(60.1, "IN ", fix(V, C, 54, "5", 60.1, [(58, "MsgSeqNum too low, expecting 40 but received 1")])),
    ]


def sample_garbled() -> list[str]:
    msgs = [
        fix(C, V, 1, "A", 0.0, logon(reset=True)),
        fix(V, C, 1, "A", 0.1, logon(reset=True)),
        fix(C, V, 2, "D", 1.0, nos("G-1"), bad_checksum=True),
        fix(C, V, 3, "D", 1.5, nos("G-2", px="4.30")),
        fix(V, C, 2, "2", 1.5, [(7, "2"), (16, "0")]),
        fix(C, V, 2, "D", 1.6, nos("G-1"), poss_dup=True, orig_t=1.0),
        fix(V, C, 3, "8", 1.6, er("TW10", "G-1", "E100", "0", "0", 10, 0, 10)),
        fix(C, V, 3, "D", 1.6, nos("G-2", px="4.30"), poss_dup=True, orig_t=1.5),
        fix(V, C, 4, "8", 1.7, er("TW11", "G-2", "E101", "0", "0", 10, 0, 10)),
        fix(C, V, 4, "D", 2.0, nos("G-3", px="4.35"), bad_length=5),
    ]
    # Plain '|' delimited, one message per line, no timestamps: how logs get pasted into tickets.
    return [m.replace(SOH, "|") for m in msgs]


def sample_state_machine() -> list[str]:
    return [
        quickfix_line(0.0, "OUT", fix(C, V, 1, "A", 0.0, logon(reset=True))),
        quickfix_line(0.1, "IN ", fix(V, C, 1, "A", 0.1, logon(reset=True))),
        quickfix_line(1.0, "OUT", fix(C, V, 2, "D", 1.0, nos("S-1"))),
        quickfix_line(1.1, "IN ", fix(V, C, 2, "8", 1.1, er("TW20", "S-1", "E200", "0", "0", 10, 0, 10))),
        quickfix_line(1.2, "IN ", fix(V, C, 3, "8", 1.2, er("TW20", "S-1", "E201", "F", "1", 10, 6, 4, 6, "4.25", "4.25"))),
        quickfix_line(1.3, "IN ", fix(V, C, 4, "8", 1.3, er("TW20", "S-1", "E202", "F", "2", 10, 10, 0, 4, "4.26", "4.254"))),
        # Bug: a late partial fill after the order was already Filled.
        quickfix_line(1.4, "IN ", fix(V, C, 5, "8", 1.4, er("TW20", "S-1", "E203", "F", "1", 10, 8, 2, 2, "4.27", "4.26"))),
        quickfix_line(2.0, "OUT", fix(C, V, 3, "D", 2.0, nos("S-2", qty=10, px="4.10"))),
        quickfix_line(2.1, "IN ", fix(V, C, 6, "8", 2.1, er("TW21", "S-2", "E204", "0", "0", 10, 0, 10))),
        # Bug: LeavesQty not recomputed after the fill (5 + 3 != 10), and ExecID reused.
        quickfix_line(2.2, "IN ", fix(V, C, 7, "8", 2.2, er("TW21", "S-2", "E205", "F", "1", 10, 5, 3, 5, "4.10", "4.10"))),
        quickfix_line(2.3, "IN ", fix(V, C, 8, "8", 2.3, er("TW21", "S-2", "E205", "F", "1", 10, 7, 3, 2, "4.10", "4.10"))),
        quickfix_line(3.0, "OUT", fix(C, V, 4, "F", 3.0, [(41, "S-9"), (11, "S-9-C"), (55, "SPY"), (54, "1"), (60, ts(3.0)), (38, "10")])),
        quickfix_line(3.1, "IN ", fix(V, C, 9, "9", 3.1, [(37, "NONE"), (11, "S-9-C"), (41, "S-9"), (39, "8"), (434, "1"), (102, "1"), (58, "Unknown order: OrigClOrdID S-9")])),
    ]


def sample_no_acks() -> list[str]:
    return [
        quickfix_line(0.0, "OUT", fix(C, V, 1, "A", 0.0, logon(hb=30, reset=True))),
        quickfix_line(0.1, "IN ", fix(V, C, 1, "A", 0.1, logon(hb=30, reset=True))),
        # Symbol missing: rejected at the session level, never reaches the OMS.
        quickfix_line(1.0, "OUT", fix(C, V, 2, "D", 1.0, nos("N-1", symbol=None))),
        quickfix_line(1.1, "IN ", fix(V, C, 2, "3", 1.1, [(45, "2"), (371, "55"), (372, "D"), (373, "1"), (58, "Required tag missing: Symbol(55)")])),
        quickfix_line(2.0, "OUT", fix(C, V, 3, "D", 2.0, nos("N-2", px="4.40"))),
        # No ExecutionReport for N-2, and then the venue goes quiet for 50s with HeartBtInt=30.
        quickfix_line(31.0, "OUT", fix(C, V, 4, "0", 31.0, [])),
        quickfix_line(52.0, "IN ", fix(V, C, 3, "0", 52.0, [])),
        quickfix_line(61.0, "OUT", fix(C, V, 5, "1", 61.0, [(112, "TEST-1")])),
        quickfix_line(61.2, "IN ", fix(V, C, 4, "0", 61.2, [(112, "TEST-1")])),
    ]


def sample_spread() -> list[str]:
    legs = [(555, "2"),
            (600, "SPY"), (602, "SPY   250620C00560000"), (603, "8"), (608, "OCXXXS"), (609, "OPT"), (611, "20250620"), (612, "560"), (623, "1"), (624, "1"),
            (600, "SPY"), (602, "SPY   250620C00565000"), (603, "8"), (608, "OCXXXS"), (609, "OPT"), (611, "20250620"), (612, "565"), (623, "1"), (624, "2")]
    ab = [(11, "SP-1"), (54, "1"), (55, "SPY")] + legs + [(60, ts(1.0)), (38, "3"), (40, "2"), (44, "1.85"), (59, "0")]

    def strat(exec_id, exec_type, status, cum, leaves, last_qty=0, last_px="0", avg="0"):
        body = [(37, "TW30"), (11, "SP-1"), (17, exec_id), (150, exec_type), (39, status), (442, "3"), (55, "SPY"), (54, "1"), (38, "3"), (40, "2"), (44, "1.85")]
        if last_qty:
            body += [(32, str(last_qty)), (31, last_px)]
        return body + [(151, str(leaves)), (14, str(cum)), (6, avg), (60, ts(1.1))] + legs

    def leg(exec_id, strike, side, qty, px, cum, leaves):
        return [(37, "TW30"), (11, "SP-1"), (17, exec_id), (150, "F"), (39, "2" if leaves == 0 else "1"), (442, "2"), (55, "SPY"),
                (167, "OPT"), (541, "20250620"), (201, "1"), (202, strike), (54, side), (38, str(qty)), (40, "2"),
                (32, str(qty)), (31, px), (151, str(leaves)), (14, str(cum)), (6, "1.85"), (60, ts(1.1))]

    return [
        quickfix_line(0.0, "OUT", fix(C, V, 1, "A", 0.0, logon(reset=True))),
        quickfix_line(0.1, "IN ", fix(V, C, 1, "A", 0.1, logon(reset=True))),
        quickfix_line(1.0, "OUT", fix(C, V, 2, "AB", 1.0, ab)),
        quickfix_line(1.1, "IN ", fix(V, C, 2, "8", 1.1, strat("E300", "A", "A", 0, 3))),
        quickfix_line(1.1, "IN ", fix(V, C, 3, "8", 1.1, strat("E301", "0", "0", 0, 3))),
        quickfix_line(1.1, "IN ", fix(V, C, 4, "8", 1.1, strat("E302", "F", "2", 3, 0, 3, "1.85", "1.85"))),
        quickfix_line(1.1, "IN ", fix(V, C, 5, "8", 1.1, leg("E303", "560", "1", 3, "4.39", 3, 0))),
        quickfix_line(1.1, "IN ", fix(V, C, 6, "8", 1.1, leg("E304", "565", "2", 3, "2.54", 3, 0))),
    ]


SAMPLES = {
    "01-gap-recovered-and-unfilled.log": (sample_gap, "A fill is lost and recovered through ResendRequest; later two messages vanish for good."),
    "02-sequence-too-low.log": (sample_seq_too_low, "A client restarts without its message store and logs on from seq 1."),
    "03-garbled-messages.log": (sample_garbled, "One order with a bad CheckSum, one with a bad BodyLength; '|' delimited, as pasted into a ticket."),
    "04-broken-state-machine.log": (sample_state_machine, "A counterparty's OMS reports a fill after Filled, a LeavesQty that doesn't add up and a reused ExecID."),
    "05-orders-not-acking.log": (sample_no_acks, "\"My orders aren't acking\": a session reject, a silent venue and a heartbeat timeout."),
    "06-clean-vertical-spread.log": (sample_spread, "A clean NewOrderMultileg vertical: strategy reports plus leg fills. Nothing should be flagged."),
}


def main() -> None:
    OUT.mkdir(exist_ok=True)
    index = []
    for name, (build, description) in SAMPLES.items():
        (OUT / name).write_text("\n".join(build()) + "\n", encoding="ascii")
        index.append({"file": name, "description": description})
    (OUT / "index.json").write_text(json.dumps(index, indent=2) + "\n")
    print(f"wrote {len(SAMPLES)} samples to {OUT}")


if __name__ == "__main__":
    main()
