# EFT 1.1 Profile Selection Log Analysis

This document records what a controlled EFT `1.1.0.0.46657` menu session revealed
about session-mode and profile-selection logs. It is an evidence record for parser
design, not a claim that later EFT versions use the same contract.

For the procedure used to launch the game and collect this evidence, see
[eft-live-log-capture-runbook.md](eft-live-log-capture-runbook.md).

## Capture scope

| Field | Value |
| --- | --- |
| Capture date | 2026-08-09 |
| Client version | `1.1.0.0.46657` |
| Session directory | `log_2026.08.09_14-34-20_1.1.0.0.46657` |
| Source | The session's `application` log |
| Controlled order | PvE Zone -> PvP Season -> PvP Zone -> PvE Zone |
| Raid or matchmaking | Not entered |
| Final state | Original PvE Zone profile restored before exit |

The same game process was used for all transitions. Profile and account ids were
compared in memory and then discarded; only relationships between them are
recorded here. The raw log is deliberately not part of the repository.

## Log discovery findings

This client created its session directly under:

```text
<EFT installation>\Logs\log_2026.08.09_14-34-20_1.1.0.0.46657\
```

That observed location differs from older references to
`<EFT installation>\build\Logs` or `%LOCALAPPDATA%`. The active client's newly
created session directory is stronger evidence than a hard-coded historical
path.

BSG Launcher's `settings` file contained a stale `gamesRootDir` on the capture
machine. The launcher's own logs contained the current executable/install path.
Path discovery should therefore use this order:

1. Treat the settings value as a candidate, not an authority.
2. Use path-only matches from recent BSG Launcher logs when the candidate is
   missing or stale.
3. Confirm the result by observing a new EFT session directory after launch.

The session directory name embeds the client version. The `application` log was
the relevant source for all findings below; no network-log evidence was needed
because the capture never entered matching or a raid.

## Redacted evidence

The relevant lines appeared in this order:

```text
14:34:52.748 Session mode: Pve

14:35:58.552 Session mode: PvpSeason
14:36:04.835 PrepareSelectedProfileLocally ProfileId:<season-pmc-id> AccountId:<account-id>
14:36:04.835 CompleteSelectedProfile ProfileId:<season-pmc-id> AccountId:<account-id>

14:38:25.148 Session mode: Regular
14:38:31.536 PrepareSelectedProfileLocally ProfileId:<permanent-pvp-pmc-id> AccountId:<account-id>
14:38:31.536 CompleteSelectedProfile ProfileId:<permanent-pvp-pmc-id> AccountId:<account-id>

14:39:57.638 Session mode: Pve
14:40:03.807 PrepareSelectedProfileLocally ProfileId:<pve-pmc-id> AccountId:<account-id>
14:40:03.807 CompleteSelectedProfile ProfileId:<pve-pmc-id> AccountId:<account-id>
```

The real lines also contained the date, client version, log level, and
`application` category. Those stable prefixes are omitted above to keep the state
transitions readable.

## Findings

### Session mode is a semantic profile hint

The controlled UI selections produced these exact tokens:

| Selected EFT profile | `Session mode` token | Game rules |
| --- | --- | --- |
| PvE Zone | `Pve` | PvE |
| PvP Season | `PvpSeason` | PvP |
| PvP Zone | `Regular` | PvP |

`PvpSeason` is positive seasonal evidence in this client build. It must remain
separate from the broader PvP/PvE rules value because both `PvpSeason` and
`Regular` imply PvP rules while selecting different characters and different
application storage profiles.

This capture did not produce `Session mode: Pvp`. Existing compatibility support
for `Pvp` comes from older logs, not from this session.

### Session-mode parsing needs an exact token boundary

The capture exposed a prefix bug in the repository's existing expression:

```regex
Session mode: (Pve|Pvp|Regular)
```

Because the expression is not bounded, `Session mode: PvpSeason` succeeds as
`Pvp`. The ordering of alternatives does not make the result safe. The observed
tokens require a whole-token expression such as:

```regex
Session mode:\s*(Pve|PvpSeason|Pvp|Regular)\s*$
```

Parsing should map the exact token to two facts when callers need both:

| Token | Session profile hint | Game mode |
| --- | --- | --- |
| `Pve` | PvE Zone | PVE |
| `Regular` | PvP Zone | PVP |
| `Pvp` | PvP Zone, legacy compatibility | PVP |
| `PvpSeason` | PvP Season | PVP |

Unknown future tokens should remain unknown rather than falling through to a
shorter known prefix.

### EFT 1.1 profile selection is a two-line completion sequence

Every completed switch in this capture emitted
`PrepareSelectedProfileLocally` immediately followed by
`CompleteSelectedProfile`. The two lines in each pair had the same timestamp and
the same profile/account values. No legacy `SelectProfile` line appeared.

The repository's capture-time `EftRaidEventService` recognizes only
`SelectProfile`, so it cannot refresh the PMC identity from these EFT 1.1 switches.
A compatible profile-selection expression is:

```regex
(?:SelectProfile|CompleteSelectedProfile) ProfileId:([a-f0-9]+) AccountId:(\d+)
```

`CompleteSelectedProfile` is the commit point for the new sequence.
`PrepareSelectedProfileLocally` describes preparation and should not publish a
profile change. Although all three prepare lines completed in this capture, one
successful run does not prove that preparation is always followed by completion,
especially if a switch is interrupted.

Legacy `SelectProfile` should remain supported because this capture establishes a
new form without disproving the old form.

### Mode and identity are separate, non-atomic facts

The session-mode line preceded the completed profile-selection line on every
controlled switch:

| Transition target | Mode-to-completion delay |
| --- | ---: |
| PvP Season | 6.283 seconds |
| PvP Zone | 6.388 seconds |
| Restored PvE Zone | 6.169 seconds |

A parser must therefore tolerate a short period in which the new semantic mode is
known but the completed PMC identity is not. It should not assume that both facts
arrive on one line or in one filesystem notification.

The initial `Pve` mode line was not followed by a profile-selection line in the
filtered startup sequence. Startup scanning must independently retain the latest
valid mode and latest completed profile identity rather than requiring them to be
adjacent.

### Profile ids distinguish the three characters but do not classify them

The completed seasonal-PvP, permanent-PvP, and PvE PMC ids were all distinct. All
three lines carried the same Account ID. This establishes the following for the
captured account and build:

- One EFT account can own distinct PMC identities for PvE Zone, PvP Zone, and PvP
  Season.
- Account ID is not sufficient as an application profile or character key.
- The opaque shape of a Profile ID does not reveal which profile kind it belongs
  to. The labels in this analysis are known only because each id was captured
  after a controlled UI selection.
- A mode token and a completed Profile ID answer different questions: the token
  classifies the selected rules/profile kind, while the id identifies the
  character instance.

No actual identifier should be placed in a parser fixture. Use distinct named
placeholders or generated 24-character hex values and assert only the required
relationships.

## Parser contract derived from the capture

For both startup scans and live tailing:

1. Match the complete `Session mode` token.
2. Preserve seasonal profile classification separately from PvP/PvE game rules.
3. Accept `CompleteSelectedProfile` as the EFT 1.1 committed PMC-selection event.
4. Keep legacy `SelectProfile` compatibility.
5. Ignore `PrepareSelectedProfileLocally` for state publication.
6. Allow mode and completed identity events to arrive several seconds apart.
7. Keep the previous valid state for unknown or partial input.
8. Use the same parsing rules for historical startup scans and appended live lines.

A minimal regression fixture should cover:

- `PvpSeason` is not parsed as `Pvp`.
- `PvpSeason` yields a seasonal hint and PVP rules.
- `Regular` yields a permanent-PvP hint and PVP rules.
- `Pve` yields a PvE hint and PVE rules.
- A prepare-only line does not update selected identity.
- `CompleteSelectedProfile` and legacy `SelectProfile` do update identity.
- The three redacted PMC ids are distinct while their Account ID is shared.
- Mode and profile completion remain valid when unrelated lines or a delay separate
  them.

## Limits of the evidence

This was one account, one client build, and one controlled menu session. It did
not test:

- A raid, matching, reconnect, transit, PMC/Scav selection, or network logs
- An interrupted profile switch or a prepare line without completion
- The legacy `Pvp` or `SelectProfile` forms
- A second account, a later client build, or a localized client log format
- Whether seasonal profile ids persist across a wipe or future season

Consequently, `PvpSeason`, the two-line selection sequence, and the measured
timing are versioned observations. A future client change should degrade to
unknown/manual behavior until a new redacted capture updates this analysis and
its parser fixtures.

## Related documents

- [eft-live-log-capture-runbook.md](eft-live-log-capture-runbook.md): repeatable
  launch, capture, redaction, restoration, and cleanup procedure
- [eft-log-patterns.md](eft-log-patterns.md): broader EFT application and network
  log pattern reference
- [feature-seasonal-profile.spec.md](decisions/feature-seasonal-profile.spec.md):
  technical design that consumes these findings
