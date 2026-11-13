#!/usr/bin/env bash
set -euo pipefail
# This file is invoked only inside the isolated replication verification project.
host="tourbooking-rs/mongodb-a:27017,mongodb-b:27017,mongodb-c:27017"
archive="/tmp/verification-$(date -u +%Y%m%dT%H%M%S).gz"
[[ "${RESTORE_DATABASE:-}" =~ ^RestoreVerificationDB_[a-f0-9]{32}$ ]] || exit 1
mongodump --host "$host" --username mongo_admin --password "$MONGO_INITDB_ROOT_PASSWORD" \
  --authenticationDatabase admin --db VerificationDB --archive="$archive" --gzip
mongorestore --host "$host" --username mongo_admin --password "$MONGO_INITDB_ROOT_PASSWORD" \
  --authenticationDatabase admin --archive="$archive" --gzip \
  --nsInclude='VerificationDB.*' --nsFrom='VerificationDB.*' --nsTo="$RESTORE_DATABASE.*"
sha256sum "$archive"
