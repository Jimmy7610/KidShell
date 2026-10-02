# apply/ — the scripts that change the machine

Everything in this folder mutates the computer it runs on. Nothing else in the
validation toolset does.

**Do not run anything here on a machine you care about.** Each script refuses
unless all of the following hold at the same time, and each one is of a
deliberately different kind so that no single mistake can satisfy them:

| Condition | Where it comes from |
| --- | --- |
| a marker file at `C:\ProgramData\KidShell-TestDevice\ALLOW-KIDSHELL-VALIDATION.txt` | the machine |
| a confirmation phrase typed in full | the operator, at the keyboard |
| `DedicatedTestDevice = true` in `device-validation.json` | a file somebody edited on purpose |
| the config's `MachineName` matching this computer | Windows |
| an elevated process | the token |
| not a development build | the code |

On top of that, **every script here defaults to a dry run.** Passing all six
conditions gets you a description of what would happen. `-Apply` is what makes
it happen.

`00-declare-test-device.ps1` is the exception that proves the rule: it creates
the marker, so it cannot require it. Instead it requires everything else **plus**
an empty set of working-machine signs — no browser profile, no mail data, no git
checkout, no domain membership. Writing that marker is the moment a machine
becomes expendable, so it is the strictest gate in the toolset rather than the
loosest.

## Order

1. `00-declare-test-device.ps1` — write the marker
2. `01-create-child-account.ps1` — create the standard account KidShell runs as
3. `02-install-securityhost-service.ps1` — register and start the broker service
4. `03-apply-protected-store-acl.ps1` — permission `%ProgramData%\KidShell\policy`

Then go back to the numbered verifiers in the parent folder.

## What is not here

There is no script that enables AppLocker, configures Assigned Access, changes
the Windows shell, sets up auto-logon or edits registry lockdown. Those changes
belong to KidShell's own transaction model, which writes a recovery manifest
**before** it changes anything — see [RECOVERY.md](../../../docs/RECOVERY.md).
A validation script that applied them outside a transaction would be a change
with no recorded way back, which is the one thing this project will not ship.

## Undoing it

`03` and `02` are reversible from an elevated session:

```powershell
sc.exe stop KidShellSecurityHost
sc.exe delete KidShellSecurityHost
Remove-Item -Recurse -Force 'C:\ProgramData\KidShell'
```

`01` is reversible with `Remove-LocalUser`. Taking a VM snapshot before any of
it is still the better plan, and the runbook says so first.
