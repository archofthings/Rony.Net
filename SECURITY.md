# Security Policy

## Supported versions
Security fixes are released for the latest minor version of the packages (`Rony.Net`, `Rony.Net.Xunit`,
`Rony.Net.Xunit.v3`, `Rony.Net.NUnit`, `Rony.Net.MSTest`, `Rony.Net.Testcontainers` and `Rony.Net.Cli`). Older versions are not patched: update to
the latest release.

## Reporting a vulnerability
Please do not open a public issue for a security problem.

Report it privately through GitHub: on the repository's **Security** tab choose **Report a vulnerability**
([direct link](https://github.com/archofthings/Rony.Net/security/advisories/new)). Include the version, what an attacker
can do, and the steps or a small test that shows it.

You can expect a first answer within a week. When the report is confirmed, a fix is prepared in a private advisory,
released as a new version, and the advisory is published with credit to you unless you prefer otherwise.

## Scope
Rony.Net is a mock server for tests and development. Running it, or the `rony` tool, on a network you do not trust is
outside its intended use; the limits that follow from that are listed in
[Known Issues and Limitations](https://github.com/archofthings/Rony.Net/wiki/Known-Issues) and
[Limits and security](https://github.com/archofthings/Rony.Net/wiki/Standalone-Server#limits-and-security). Reports
about those documented limits are welcome as ordinary issues.
