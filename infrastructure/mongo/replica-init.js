const admin = db.getSiblingDB("admin");
if (!admin.auth("mongo_admin", process.env.MONGO_INITDB_ROOT_PASSWORD)) {
  throw new Error("Mongo authentication failed");
}
try {
  rs.status();
} catch (error) {
  if (error.code !== 94) throw error;
  const result = rs.initiate({
    _id: "tourbooking-rs",
    members: [
      { _id: 0, host: "mongodb-a:27017", priority: 2 },
      { _id: 1, host: "mongodb-b:27017" },
      { _id: 2, host: "mongodb-c:27017" }
    ]
  });
  if (!result.ok) throw new Error("Replica set initialization failed");
}
