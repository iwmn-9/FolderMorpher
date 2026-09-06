# Security Policy

## Reporting Security Issues

We take the security and integrity of FolderMorpher seriously. If you discover a potential security vulnerability, please do not open a public issue.

Instead, please report it privately via GitHub Security Advisories or by contacting the repository owner directly.

## Safe Architecture Guarantees

- **No Network Egress**: FolderMorpher does not send telemetry, user files, directory listings, or credentials to external servers. All operations are local to the machine/network.
- **Atomic Rollback Snapshots**: Any modification to NTFS Access Control Lists (ACLs) automatically stores a SDDL rollback snapshot in memory for immediate restoration.
- **Sanctuary Safeguards**: Media and audit optimization automatically detects and skips sensitive master archives (`_Master`, `_Original`, RAW formats).
