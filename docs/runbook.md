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

### Database disk full (2026-09-30)

**Symptom:** the site shows Replay mode; `/health` hangs or returns 503; API logs show `MySqlException: Connect
Timeout expired`; even `fly ssh console -a tickwire-db` times out.

**Cause:** MySQL 8 writes a binary log by default. With every FIX message persisted, it grew about 10 MB an hour
and filled the 1 GB volume. A full disk makes MySQL block writes rather than fail, so connections hang. At the
time, Fly's health check was `/health`, which included MySQL, so the proxy took the API out of rotation even though
trading runs in memory.

**Fix applied:** `--disable-log-bin` in `deploy/fly.db.toml` (single instance, no replicas); the old binlogs were
deleted (disk went from 88% to 41%). Fly now checks `/health/live` (process only). `/health` reports MySQL
trouble as `Degraded` within 3 seconds, and the smoke test still alerts on anything but `Healthy`. Housekeeping
deletes wire-archive rows older than 3 days and audit rows older than 30, in 5,000-row batches.

**If it happens again:**
1. `fly machine exec <id> "df -h /var/lib/mysql" -a tickwire-db` (works when SSH doesn't).
2. `fly machine restart <id> -a tickwire-db` to unstick MySQL; the API reconnects and flushes what it buffered.
3. Find what's using the space before deleting anything; `fly volumes extend` is the fallback if it's real data.
