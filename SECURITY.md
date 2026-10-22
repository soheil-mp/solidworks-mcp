# Security policy

solidworks-mcp runs locally and drives the user's SOLIDWORKS with the user's rights. The main risks are:
- **Prompt injection.** Text from models, properties, file names or the web can steer an agent. `vba_run` is
  therefore off by default and marked as requiring user interaction.
- **Destructive file or document operations.** The server's ownership and path policies are designed to prevent these
  (see docs/DESIGN.md §3.7 and §3.11).

Please report vulnerabilities privately through GitHub's "Report a vulnerability" (Security tab) rather than in a
public issue. Include the server version, the SOLIDWORKS version and a minimal reproduction. Expect a reply within a
week.

The server makes no network connections of its own and collects no telemetry.
