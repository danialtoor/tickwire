# Runbook: client FIX support

For whoever is on the support desk. Each section starts from what the client says, not from what's wrong.

Tools: the **Ops dashboard** (`/ops`), the **FIX Inspector** (per session), the **Log Analyzer** (`/analyzer` or
`tickwire fixlog analyze`), and the PowerShell module (`Import-Module ./tools/powershell/Tickwire.Admin`).

---

## "My orders aren't acking"

1. **Is the session logged on?**
   `Get-FixSession -ClientCompId BYO-XXXX` (or the Ops page). `Active` means Logon completed. Anything else: go to
   *"We can't log on"*.
2. **Did the orders reach us?** Export the venue side of the session and analyze it:
   ```powershell
   Export-FixLog -ClientCompId BYO-XXXX -Path ./case-1234.log
   Invoke-FixLogAnalysis ./case-1234.log | Format-Table Severity, Code, Title
   ```
   | Finding | Meaning | Tell the client |
   |---|---|---|
   | `SESSION_REJECT` + `NO_ACK` | we answered with Reject(3); the order never reached the OMS | the field in 371/58, e.g. "Symbol(55) missing" |
   | `GARBLED` | the order's CheckSum/BodyLength was wrong, so we ignored it | check what rewrites their messages (encoding, proxy) |
   | `SEQ_GAP` on their side | their orders are queued behind a gap they never filled | answer our ResendRequest, or reset with 141=Y |
   | `ORDER_REJECTED` | risk reject (ExecType 8) | the Text(58): limits, price band, kill switch |
   | `NO_ACK` alone | nothing arrived from them at all | have them check they're connected to `TICKWIRE` on the right host/port |
3. **Did we send acks that they didn't see?** Analyze *their* log too. A `SEQ_GAP` on our flow in their log means
   our reports are queued behind a gap on their side; their engine should have sent ResendRequest.
4. **Is the client or the whole venue blocked?** Kill switch state is on the Ops page. A global kill shows
   "GLOBAL KILL SWITCH ON".

## "We can't log on"

The venue always explains a refused Logon in a Logout's Text(58) (or, before a session exists, a best-effort Logout):

| Text | Fix |
|---|---|
| `Unknown session X->Y` | wrong SenderCompID/TargetCompID, or credentials not provisioned / expired |
| `MsgSeqNum too low, expecting N but received M` | their store was lost or reset; log on with `141=Y`, or `Reset-FixSession` |
| `HeartBtInt must be between 1 and 300` | fix 108 |
| `Unsupported EncryptMethod, use 98=0` | fix 98 |
| `Session is already logged on from another connection` | a second instance is running; stop one |
| connection closes with no Logout | first message wasn't a Logon, or nothing was sent for 10 s |

## "We keep getting disconnected"

Look for `HEARTBEAT_EXCEEDED` and `LOGOUT_REASON` ("Heartbeat timeout") in the analyzer. The client isn't
answering TestRequest(1) or isn't sending Heartbeats: usually a blocked event loop, GC pauses, or a NAT/firewall
idle timeout shorter than HeartBtInt.

## "Our positions don't match your fills"

Analyze their log: `DUPLICATE_EXECID`, `FILL_SUM_MISMATCH`, `CUMQTY_DECREASED`, `STATE_REGRESSION` point at the
side that's wrong. Our ExecIDs are unique across restarts; reconcile by ExecID, not by ClOrdID.

## Kill switch

```powershell
Invoke-KillSwitch -Client gst-7q2k            # one client: cancels open orders, blocks new ones
Invoke-KillSwitch -Client gst-7q2k -Release
Connect-Tickwire -AdminKey $key
Invoke-KillSwitch -Global                     # everyone; asks for confirmation
```

## Deploys and incidents

- Deploys go out from `main` after CI passes (`.github/workflows/deploy.yml`). Roll back with
  `fly releases -a tickwire-api` then `fly deploy --image <previous image>`.
- The production smoke test runs every 30 minutes and opens an `incident` issue when it fails. First checks:
  `fly status -a tickwire-api`, `fly logs -a tickwire-api`, `fly status -a tickwire-db`.
- Restarting the API is safe: sequence numbers, the resend store, and client config are in MySQL
  (`fly machine restart -a tickwire-api`). Connected clients reconnect and continue from their stored sequence
  numbers.
- If the API is down, the website switches to Replay mode on its own, and the decoder and analyzer keep working in
  the browser.
