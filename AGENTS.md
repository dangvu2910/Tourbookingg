# Project instructions

Read and follow `AI-rules/AGENTS.md` and `AI-rules/api-guidelines.md` before changing this repository. Preserve existing databases and the legacy MVC routes. The SQL Server/MongoDB stack uses `compose.hybrid.yaml`; The old SQLite stack (`compose.yaml`) was removed on 2026-11-13 after every row was verified in SQL Server/MongoDB (SQLite archives deleted); back up with `scripts/Backup.ps1` before risky data work.

Locally each service runs as one container on one Compose network with Kong as the only entry. There is no cloud deployment target (AWS plans were dropped on 2026-11-13); do not introduce Docker Swarm or cloud infrastructure unless asked.
