// Official mongo image runs this only on first initialization of its named volume.
for (const [database, user, variable] of [
  ["ChatDB", "chat_app", "MONGO_CHAT_PASSWORD"],
  ["NotificationDB", "notification_app", "MONGO_NOTIFICATION_PASSWORD"]
]) {
  const password = process.env[variable];
  if (!password) throw new Error(`Missing ${variable}`);
  db.getSiblingDB(database).createUser({ user, pwd: password, roles: [{ role: "readWrite", db: database }] });
}
