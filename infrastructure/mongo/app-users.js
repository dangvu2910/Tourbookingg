const admin = db.getSiblingDB("admin");
if (!admin.auth("mongo_admin", process.env.MONGO_INITDB_ROOT_PASSWORD)) throw new Error("Authentication failed");
try { rs.status(); } catch (error) {
  if (error.code !== 94) throw error;
  rs.initiate({ _id: "tourbooking-rs", members: [
    { _id: 0, host: "mongodb-a:27017", priority: 2 },
    { _id: 1, host: "mongodb-b:27017" }, { _id: 2, host: "mongodb-c:27017" }
  ] });
}
let primary;
for (let attempt = 0; attempt < 120; attempt++) {
  primary = db.hello().primary;
  if (primary) break;
  sleep(1000);
}
if (!primary) throw new Error("Mongo primary election timed out");
const connection = new Mongo("mongodb://mongo_admin:" + encodeURIComponent(process.env.MONGO_INITDB_ROOT_PASSWORD) + "@" + primary + "/admin");
for (const [database, user, variable] of [
  ["ChatDB", "chat_app", "MONGO_CHAT_PASSWORD"],
  ["NotificationDB", "notification_app", "MONGO_NOTIFICATION_PASSWORD"]
]) {
  const password = process.env[variable];
  if (!password) throw new Error("Missing application credential");
  const owned = connection.getDB(database);
  if (!owned.getUser(user)) owned.createUser({ user, pwd: password, roles: [{ role: "readWrite", db: database }] });
}
print("Mongo application users provisioned on replica set primary");
