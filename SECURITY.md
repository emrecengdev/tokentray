# Security policy

TokenTray reads sign-in tokens for AI coding tools on your PC, so security reports matter.

**Please report vulnerabilities privately** through GitHub's [private vulnerability reporting](https://github.com/emrecengdev/tokentray/security/advisories/new), not in a public issue. Include the version, steps to reproduce and the impact. Remove any real tokens, emails or account ids from what you send.

You can expect a first reply within a few days. Fixes ship as a new release with a note in its changelog.

## What TokenTray does with credentials

- Reads them; never refreshes, stores copies of, or writes them.
- Sends each token only to its own tool's usage endpoint over HTTPS.
- Decrypts the Claude desktop app's sign-in with Windows DPAPI for the current user, and uses it only when it belongs to the same account as the Claude Code CLI.
- Keeps no logs of tokens. The optional error log in `%TEMP%\TokenTray.log` holds exceptions only.
