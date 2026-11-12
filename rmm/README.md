# DumpTriage from any RMM

An RMM tool already knows a device crashed. DumpTriage adds what the crash was. Any RMM that can run a script on a
schedule can send the dump: the upload is one HTTPS call, and nothing here installs a plug-in. The only recipe written
for a specific RMM so far is [NinjaOne](ninjaone/README.md); this page is what the others need.

## What your RMM has to do

1. **Hold an upload-only key per client**, in a secret field the script can read (not on the command line, where
   process listings and logs show it). Mint it with the account's primary key; an upload-only key can call
   `POST /api/v1/dumps` and nothing else, so a key read off one device cannot list or download anything, and revoking
   one client's key affects nobody else. An account holds at most 20 active named keys (the 21st is refused with 409),
   so past 20 clients let one key serve a group of them and tell the clients apart with the `reference` below:

   ```sh
   curl -X POST https://api.performanceinsights.ai/api/v1/customers/me/api-keys \
     -H "Authorization: Bearer <primary key>" -H "Content-Type: application/json" \
     -d '{"name": "acme", "scope": "upload"}'
   # -> {"id": "...", "api_key": "dts_...", "scope": "upload", ...}  (shown once)
   ```

2. **Run a script on a schedule** (every few hours is enough: a crash writes its dump during the reboot) with
   system rights, so it can read the dump folders.
3. **Send each new dump once.** Remember what was sent (path, size and time) and skip it next run. If the state is
   lost the service recognises the same bytes and does not charge or analyse twice.
4. **Put the `report_url` where the technician looks**: a custom field, a ticket note or the activity log.

## The upload

```sh
status=$(curl -sS -H 'Expect:' -o resp.json -w '%{http_code}' \
  -H "Authorization: Bearer $DUMPTRIAGE_API_KEY" \
  --form-string "hostname=$(hostname)" \
  --form-string "reference=client=Acme;ticket=INC-4021" \
  -F "file=@/var/crash/core.1234" \
  https://api.performanceinsights.ai/api/v1/dumps)
# 201, and resp.json has {"id": "...", "report_url": "https://.../dumps/...", "status": "...", ...}
```

- `-H 'Expect:'`: curl adds `Expect: 100-continue` to large uploads and the API answers 417.
- `--form-string` for `reference`: `-F` reads a `;` as the start of an option, so `client=Acme;ticket=INC-4021` would be
  cut at the `;`.
- The text fields come before `file` on purpose: a label that is too long is refused before the dump is read, not
  after it has been uploaded.
- `hostname` is turned into a keyed hash on our side and never stored; leave it out to send nothing.

What to do with the status (the body says why):

| Status | Meaning | Do |
|---|---|---|
| `201` | Stored (a dump we already have answers `200`/`201` too, no second charge) | Record it, use `report_url` |
| `401` | The key is unknown or revoked | Stop and alert |
| `403` | The key cannot upload, or the account needs an action (terms not accepted, account closed) | Stop and alert; read the body |
| `429` | Quota, or too many uploads at once | Retry next run |
| `5xx`, no answer | Outage or network | Retry next run |
| `400`, `413`, `422` | Not a dump, a field refused, too large for the plan, or a dump type the service does not take yet | Record it and do not resend every run; some of these clear later (a plan change, a release), so make it easy to clear |

The [API reference](https://performanceinsights.ai/api-docs/) has the rest.

## Which script on which OS

| OS | Use | Notes |
|---|---|---|
| Windows | [`ninjaone/Invoke-DumpTriageCollect.ps1`](ninjaone/Invoke-DumpTriageCollect.ps1) | Runs in any RMM that can run PowerShell as SYSTEM, not only NinjaOne. Outside NinjaOne it reads the key from `DUMPTRIAGE_API_KEY`; set `DUMPTRIAGE_REFERENCE` or pass `-Reference`. Exit codes 0 to 3 tell the RMM what happened. It writes the link with `Ninja-Property-Set` only where that exists, so in another RMM read the link from its output |
| Windows, always on | [`windows/dumptriage-agent`](../windows/dumptriage-agent/README.md) | A service that watches for dumps. Unattended install with `-ApiKeyFile` |
| Linux | [`linux/dumptriage-agent`](../linux/dumptriage-agent/README.md) | A service or cron job, installed unattended with `--api-key-file` or `--api-key-stdin`. Or the `curl` above over the files in `/var/crash` |
| macOS | the `curl` above | [`macos/dumptriage-collect.sh`](../macos/dumptriage-collect.sh) asks before it sends, so it is not for unattended runs |

The two agents do not take a `reference` yet, and the macOS script does not either.

## One receiver for every client: `reference`

An account has one webhook URL, and the delivery body is `{dump_id, status, report_url}` (plus `connector_group_url`
for a dump that came through a crash-platform connector). Send your own label with each dump and it comes back as
`reference` (absent when none was sent, and on the dump itself, `GET /api/v1/dumps/{id}`), so one receiver can route
each report to the right client or PSA ticket without keeping a table of dump ids:

```
client=Acme;ticket=INC-4021
```

At most 200 characters, not read by the analysis. It is trimmed and put on one line (runs of white space become one
space, invisible format characters are dropped), so match on that form. It is stored until the dump expires, as you
wrote it apart from that, unlike `hostname`, which is only ever a keyed hash on our side: keep machine and user names
out of it. Sending the same dump again with a different `reference` re-labels it and sends no new webhook, so keep
the "already sent" state per client. A service older than the release that added it ignores the
field, so a script can send it before then. Register the receiver with `POST /api/v1/customers/me/webhook`; each
delivery is signed (see the API reference).

## Checking a new RMM

Per RMM, find out and write down: how a script runs on a schedule and as which user, where a secret per client can
live, where a result link can be written, how a non-zero exit is alerted on, and whether it has a script library to
list the recipe in. Try it with a dry run on one device first. Then add a recipe next to `ninjaone/`.
