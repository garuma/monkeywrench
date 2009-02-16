/*
 *
 * Contact:
 *   Moonlight List (moonlight-list@lists.ximian.com)
 *
 * Copyright 2008 Novell, Inc. (http://www.novell.com)
 *
 * See the LICENSE file included with the distribution for details.
 *
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using Npgsql;
using NpgsqlTypes;

namespace Builder
{
	public class DB : IDisposable
	{
		NpgsqlConnection dbcon;
		LargeObjectManager manager;
		TimeSpan db_time_difference;

		public LargeObjectManager Manager
		{
			get
			{
				if (manager == null)
					manager = new LargeObjectManager (dbcon);
				return manager;
			}
		}


		public IDbConnection Connection
		{
			get { return dbcon; }
		}

		private DB ()
		{
		}

		public DB (bool Connect)
		{
			if (Connect)
				this.Connect ();
		}

		public static void CreateParameter (IDbCommand cmd, string name, object value)
		{
			IDbDataParameter result = cmd.CreateParameter ();
			result.ParameterName = name;
			result.Value = value;
			cmd.Parameters.Add (result);
		}

		private void Connect ()
		{
			try {
				string connectionString;
				string port = Environment.GetEnvironmentVariable ("BUILDER_DATABASE_PORT");
				string server = Environment.GetEnvironmentVariable ("BUILDER_DATABASE_SERVER");

				if (!string.IsNullOrEmpty (server))
					connectionString = "Server=" + server + ";";
				else
					connectionString = "Server=localhost;";

				connectionString += "Database=builder;User ID=builder;";

				if (!string.IsNullOrEmpty (port))
					connectionString += "Port=" + port + ";";

				dbcon = new NpgsqlConnection (connectionString);

				Logger.Log ("Database connection string: {0}", connectionString);

				dbcon.Open ();

				DateTime db_now = (DateTime) ExecuteScalar ("SELECT now();");
				DateTime machine_now = DateTime.Now;
				const string format = "yyyy/MM/dd HH:mm:ss.ffff";
				db_time_difference = db_now - machine_now;

				Logger.Log ("DB now: {0}, current machine's now: {1}, adjusted now: {3}, diff: {2} ms", db_now.ToString (format), machine_now.ToString (format), db_time_difference.TotalMilliseconds, Now.ToString (format));
			} catch {
				if (dbcon != null) {
					dbcon.Dispose ();
					dbcon = null;
				}
				throw;
			}
		}

		public void Dispose ()
		{
			if (dbcon != null) {
				dbcon.Close ();
				dbcon = null;
			}
		}

		private string MD5BytesToString (byte [] bytes)
		{
			StringBuilder result = new StringBuilder (16);
			for (int i = 0; i < bytes.Length; i++)
				result.Append (bytes [i].ToString ("x2"));
			return result.ToString ();
		}

		private class DBFileStream : Stream
		{
			IDbTransaction transaction = null;
			LargeObject obj;

			public DBFileStream (DBFile file, DB db)
			{
				try {
					transaction = db.dbcon.BeginTransaction ();
					obj = db.Manager.Open (file.file_id);
				} catch {
					if (transaction != null) {
						transaction.Rollback ();
						transaction = null;
					}
				}
			}

			protected override void Dispose (bool disposing)
			{
				base.Dispose (disposing);

				if (transaction != null) {
					transaction.Rollback ();
					transaction = null;
				}
			}

			public override int Read (byte [] buffer, int offset, int count)
			{
				return obj.Read (buffer, offset, count);
			}

			public override void Write (byte [] buffer, int offset, int count)
			{
				obj.Write (buffer, offset, count);
			}

			public override long Seek (long offset, SeekOrigin origin)
			{
				throw new NotImplementedException ();
			}

			public override void SetLength (long value)
			{
				throw new NotImplementedException ();
			}

			public override void Flush ()
			{
				// nop
			}

			public override long Position
			{
				get
				{
					return obj.Tell ();
				}
				set
				{
					throw new NotImplementedException ();
				}
			}

			public override bool CanRead
			{
				get { return true; }
			}

			public override bool CanWrite
			{
				get { return true; }
			}

			public override bool CanSeek
			{
				get { return false; }
			}

			public override long Length
			{
				get { return obj.Size (); }
			}
		}

		public Stream Download (DBFile file)
		{
			return new DBFileStream (file, this);
		}

		public Stream Download (DBWorkFileView file)
		{
			return new DBFileStream (new DBFile (this, file.file_id), this);
		}

		public int GetSize (DBFile file)
		{
			return GetLargeObjectSize (file.file_id);
		}

		public int GetSize (int file_id)
		{
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				object o = ExecuteScalar ("SELECT file_id FROM File where File.id = " + file_id.ToString ());
				
				if (!(o is int))
					throw new Exception ("File_id doesn't exist.");

				return GetLargeObjectSize ((int) o);
			}
		}

		public int GetLargeObjectSize (int oid)
		{
			Console.WriteLine ("GetLargeObjectSize ({0})", oid);
			using (IDbTransaction transaction = Connection.BeginTransaction ()) {
				int result;
				LargeObject obj = Manager.Open (oid);
				result = obj.Size ();
				obj.Close ();
				return result;
			}
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="Filename"></param>
		/// <returns></returns>
		public DBFile Upload (string Filename, bool hidden)
		{
			IDbTransaction transaction = null;
			LargeObjectManager manager;
			LargeObject obj;
			int oid;
			string md5;
			DBFile result;
			long filesize;
			string gzFilename = null;

			try {
				filesize = new FileInfo (Filename).Length;
				if (filesize > 1024 * 1024 * 100)
					throw new Exception ("Max file size is 100 MB");

				// first check if the file is already in the database
				using (MD5CryptoServiceProvider md5_provider = new MD5CryptoServiceProvider ()) {
					using (FileStream st = new FileStream (Filename, FileMode.Open, FileAccess.Read, FileShare.Read)) {
						md5 = MD5BytesToString (md5_provider.ComputeHash (st));
					}
				}

				using (IDbCommand cmd = Connection.CreateCommand ()) {
					cmd.CommandText = "SELECT * FROM File WHERE md5 = '" + md5 + "'";
					using (IDataReader reader = cmd.ExecuteReader ()) {
						if (reader.Read ())
							return new DBFile (reader);
					}
				}

				// The file is not in the database
				// Note: there is a race condition here,
				// the same file might get added to the db before we do it here.
				// not quite sure how to deal with that except retrying the above if the insert below fails.

				gzFilename = FileManager.GZCompress (Filename);

				transaction = Connection.BeginTransaction ();

				manager = new LargeObjectManager (this.dbcon);
				oid = manager.Create (LargeObjectManager.READWRITE);
				obj = manager.Open (oid, LargeObjectManager.READWRITE);

				using (FileStream st = new FileStream (gzFilename != null ? gzFilename : Filename, FileMode.Open, FileAccess.Read, FileShare.Read)) {
					byte [] buffer = new byte [1024];
					int read = -1;
					while (read != 0) {
						read = st.Read (buffer, 0, buffer.Length);
						obj.Write (buffer, 0, read);
					}
				}
				obj.Close ();

				result = new DBFile ();
				result.file_id = oid;
				result.filename = Path.GetFileName (Filename);
				result.md5 = md5;
				result.size = (int) filesize;
				result.hidden = hidden;
				switch (Path.GetExtension (Filename).ToLower ()) {
				case ".log":
				case ".txt":
				case ".html":
				case ".htm":
					// TODO: Compress this
					result.mime = "text/plain";
					break;
				case ".png":
					result.mime = "image/png";
					break;
				case ".jpg":
					result.mime = "image/jpeg";
					break;
				case ".bmp":
					result.mime = "image/bmp";
					break;
				case ".tar":
					result.mime = "application/x-tar";
					break;
				case ".bz":
					result.mime = "application/x-bzip";
					break;
				case ".bz2":
					result.mime = "application/x-bzip2";
					break;
				case ".zip":
					result.mime = "application/zip";
					break;
				case ".gz":
					result.mime = "application/x-gzip";
					break;
				default:
					result.mime = "application/octet-stream";
					break;
				}
				if (gzFilename != null)
					result.compressed_mime = "application/x-gzip";
				result.Save (this);

				transaction.Commit ();
				transaction = null;

				return result;
			} finally {
				if (transaction != null)
					transaction.Rollback ();

				try {
					if (gzFilename != null && File.Exists (gzFilename)) {
						File.Delete (gzFilename);
					}
				} catch {
					// Ignore any exceptions
				}
			}
		}

		public object ExecuteScalar (string sql)
		{
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = sql;
				return cmd.ExecuteScalar ();
			}
		}

		public DBLane CloneLane (int lane_id, string new_name)
		{
			DBLane result = null;
			DBLane master = new DBLane (this, lane_id);

			if (this.LookupLane (new_name, false) != null)
				throw new Exception (string.Format ("The lane '{0}' already exists.", new_name));

			try {
				using (IDbTransaction transaction = Connection.BeginTransaction ()) {
					result = new DBLane ();
					result.lane = new_name;
					result.max_revision = master.max_revision;
					result.min_revision = master.min_revision;
					result.repository = master.repository;
					result.source_control = master.source_control;
					result.Save (this);

					foreach (DBLanefile filemaster in master.GetFiles (this)) {
						DBLanefile clone = new DBLanefile ();
						clone.lane_id = result.id;
						clone.contents = filemaster.contents;
						clone.mime = filemaster.mime;
						clone.name = filemaster.name;
						clone.Save (this);
					}

					foreach (DBCommand cmdmaster in master.GetCommands (this)) {
						DBCommand clone = new DBCommand ();
						clone.lane_id = result.id;
						clone.alwaysexecute = cmdmaster.alwaysexecute;
						clone.arguments = cmdmaster.arguments;
						clone.command = cmdmaster.command;
						clone.filename = cmdmaster.filename;
						clone.nonfatal = cmdmaster.nonfatal;
						clone.sequence = cmdmaster.sequence;
						clone.Save (this);
					}

					foreach (DBHostLaneView hostlanemaster in master.GetHosts (this)) {
						DBHostLane clone = new DBHostLane ();
						clone.enabled = false;
						clone.lane_id = result.id;
						clone.host_id = hostlanemaster.host_id;
						clone.Save (this);
					}

					transaction.Commit ();
				}
			} catch {
				result = null;
				throw;
			}

			return result;
		}

		public DBLane LookupLane (string lane, bool throwOnError)
		{
			DBLane result = null;
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * FROM Lane WHERE lane = @lane";
				DB.CreateParameter (cmd, "lane", lane);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					if (!reader.Read ()) {
						if (!throwOnError)
							return null;
						throw new Exception (string.Format ("Could not find the lane '{0}'.", lane));
					}
					result = new DBLane (reader);
					if (reader.Read ()) {
						if (!throwOnError)
							return null;
						throw new Exception (string.Format ("Found more than one lane named '{0}'.", lane));
					}
				}
			}
			return result;
		}

		public DBLane LookupLane (string lane)
		{
			return LookupLane (lane, true);
		}

		public DBHost LookupHost (string host)
		{
			return LookupHost (host, true);
		}

		public DBHost LookupHost (string host, bool throwOnError)
		{
			DBHost result = null;
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * FROM Host WHERE host = @host";
				DB.CreateParameter (cmd, "host", host);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					if (!reader.Read ()) {
						if (!throwOnError)
							return null;
						throw new Exception (string.Format ("Could not find the host '{0}'.", host));
					}
					result = new DBHost (reader);
					if (reader.Read ()) {
						if (!throwOnError)
							return null;
						throw new Exception (string.Format ("Found more than one host named '{0}'.", host));
					}
				}
			}
			return result;
		}

		/// <summary>
		/// Returns all the lanes in the database.
		/// </summary>
		/// <returns></returns>
		public List<DBLane> GetAllLanes ()
		{
			List<DBLane> result = new List<DBLane> ();

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * FROM Lane ORDER BY lane";
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ()) {
						result.Add (new DBLane (reader));
					}
				}
			}

			return result;
		}

		public List<DBHost> GetHosts ()
		{
			List<DBHost> result = new List<DBHost> ();

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * FROM Host ORDER BY host";
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ()) {
						result.Add (new DBHost (reader));
					}
				}
			}

			return result;
		}

		public List<DBHostLane> GetAllHostLanes ()
		{
			List<DBHostLane> result = new List<DBHostLane> ();

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * FROM HostLane ORDER BY host_id, lane_id";
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ()) {
						result.Add (new DBHostLane (reader));
					}
				}
			}

			return result;
		}

		public List<DBHost> GetHostsForLane (int lane_id)
		{
			List<DBHost> result = new List<DBHost> ();

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT *, HostLane.lane_id AS lane_id FROM Host INNER JOIN HostLane ON Host.id = HostLane.host_id WHERE lane_id = @lane_id";
				DB.CreateParameter (cmd, "lane_id", lane_id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ()) {
						result.Add (new DBHost (reader));
					}
				}
			}

			return result;
		}

		public List<DBLane> GetLanesForHost (int host_id, bool only_enabled)
		{
			List<DBLane> result = new List<DBLane> ();

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT *, HostLane.host_id AS host_id, HostLane.enabled AS lane_enabled FROM Lane INNER JOIN HostLane ON Lane.id = HostLane.lane_id WHERE host_id = @host_id ";
				if (only_enabled)
					cmd.CommandText += " AND HostLane.enabled = true;";
				DB.CreateParameter (cmd, "host_id", host_id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ()) {
						result.Add (new DBLane (reader));
					}
				}
			}

			return result;
		}
		/// <summary>
		/// Returns all the lanes for which there are revisions in the database
		/// </summary>
		/// <returns></returns>
		public List<string> GetLanes ()
		{
			List<string> result = new List<string> ();

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT DISTINCT lane FROM Revision";
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ())
						result.Add (reader.GetString (0));
				}
			}

			return result;
		}

		public List<DBCommand> GetCommands (int lane_id)
		{
			List<DBCommand> result = new List<DBCommand> ();

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * FROM Command WHERE lane_id = @lane_id ORDER BY sequence ASC";
				DB.CreateParameter (cmd, "lane_id", lane_id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ())
						result.Add (new DBCommand (reader));
				}
			}

			return result;
		}

		//public List<string> GetRevisions(DBLane lane)
		//{
		//    List<string> result = new List<string>();

		//    using (IDbCommand cmd = Connection.CreateCommand())
		//    {
		//        cmd.CommandText = "SELECT DISTINCT revision FROM revisions WHERE lane = @lane ORDER BY revision DESC";
		//        DB.CreateParameter(cmd, "lane", lane);
		//        using (IDataReader reader = cmd.ExecuteReader())
		//        {
		//            while (reader.Read())
		//                result.Add(reader.GetString(0));
		//        }
		//    }

		//    return result;
		//}

		public Dictionary<string, DBRevision> GetDBRevisions (int lane_id)
		{
			Dictionary<string, DBRevision> result = new Dictionary<string, DBRevision> ();
			DBRevision rev;

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * FROM Revision WHERE lane_id = @lane_id";
				DB.CreateParameter (cmd, "lane_id", lane_id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ()) {
						rev = new DBRevision ();
						rev.Load (reader);
						result.Add (rev.revision, rev);
					}
				}
			}

			return result;
		}

		public List<DBRevision> GetDBRevisionsWithoutWork (int lane_id, int host_id)
		{
			List<DBRevision> result = new List<DBRevision> ();
			DBRevision rev;

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				//cmd.CommandText = "SELECT * FROM Revision WHERE lane_id = @lane_id AND NOT EXISTS (SELECT 1 FROM Work WHERE lane_id = @lane_id AND host_id = @host_id AND revision_id = revision.id) ORDER BY date DESC";
				cmd.CommandText = @"
SELECT Revision.*, C 
FROM 
	(SELECT RevisionWork.id, RevisionWork.revision_id, Count(Work.revisionwork_id) AS C 
		FROM RevisionWork 
		INNER JOIN work ON Work.revisionwork_id = RevisionWork.id 
		WHERE RevisionWork.lane_id = @lane_id AND RevisionWork.host_id = @host_id 
		GROUP BY RevisionWork.id, RevisionWork.revision_id) AS T 
INNER JOIN Revision ON Revision.id = T.revision_id
WHERE C = 0
ORDER BY Revision.date DESC;
";
				DB.CreateParameter (cmd, "lane_id", lane_id);
				DB.CreateParameter (cmd, "host_id", host_id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ()) {
						rev = new DBRevision ();
						rev.Load (reader);
						result.Add (rev);
					}
				}
			}

			return result;
		}

		public List<DBRevision> GetDBRevisions (int lane_id, int limit)
		{
			List<DBRevision> result = new List<DBRevision> ();
			DBRevision rev;

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT Revision.*, CAST (revision as int) AS r FROM Revision WHERE lane_id = @lane_id ORDER BY r DESC";
				if (limit > 0)
					cmd.CommandText += " LIMIT " + limit.ToString ();
				DB.CreateParameter (cmd, "lane_id", lane_id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ()) {
						rev = new DBRevision ();
						rev.Load (reader);
						result.Add (rev);
					}
				}
			}

			return result;
		}

		public List<int> GetRevisions (string lane, int limit)
		{
			List<int> result = new List<int> ();

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT DISTINCT CAST (revision as int) FROM revisions WHERE lane = @lane ORDER BY revision DESC";
				if (limit > 0)
					cmd.CommandText += " LIMIT " + limit.ToString ();
				DB.CreateParameter (cmd, "lane", lane);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ())
						result.Add (reader.GetInt32 (0));
				}
			}

			return result;
		}

		public void ClearAllWork (int lane_id, int host_id)
		{
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "UPDATE Work SET state = 0, summary = '' " +
						"WHERE lane_id = @lane_id " +
							"AND host_id = @host_id;";
				DB.CreateParameter (cmd, "lane_id", lane_id);
				DB.CreateParameter (cmd, "host_id", host_id);
				cmd.ExecuteNonQuery ();
			}
		}

		public void ClearWork (int lane_id, int revision_id, int host_id)
		{
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = @"
UPDATE 
	Work SET state = DEFAULT, summary = DEFAULT, starttime = DEFAULT, endtime = DEFAULT, duration = DEFAULT, logfile = DEFAULT, host_id = DEFAULT
WHERE
	Work.revisionwork_id IN 
		(SELECT	RevisionWork.id 
			FROM RevisionWork
			WHERE RevisionWork.host_id = @host_id AND RevisionWork.lane_id = @lane_id AND RevisionWork.revision_id = @revision_id);

UPDATE 
	RevisionWork SET state = DEFAULT, lock_expires = DEFAULT, completed = DEFAULT, workhost_id = DEFAULT
WHERE 
		lane_id = @lane_id
	AND revision_id = @revision_id 
	AND host_id = @host_id;
";
				DB.CreateParameter (cmd, "lane_id", lane_id);
				DB.CreateParameter (cmd, "revision_id", revision_id);
				DB.CreateParameter (cmd, "host_id", host_id);
				cmd.ExecuteNonQuery ();
			}
		}

		/// <summary>
		/// Deletes all the files related to the work in the revision 'revision_id' of lane 'lane' on the host 'host'.
		/// </summary>
		/// <param name="host"></param>
		/// <param name="lane"></param>
		/// <param name="revision_id"></param>
		public void DeleteFiles (DBHost host, DBLane lane, int revision_id)
		{
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = @"
DELETE FROM WorkFile
WHERE EXISTS (
	SELECT 1 
	FROM Work 
	INNER JOIN RevisionWork ON RevisionWork.id = Work.revisionwork_id
	WHERE
		RevisionWork.lane_id = @lane_id AND
		RevisionWork.host_id = @host_id AND
		RevisionWork.revision_id = @revision_id AND
		Work.id = WorkFile.work_id);
";
				DB.CreateParameter (cmd, "lane_id", lane.id);
				DB.CreateParameter (cmd, "host_id", host.id);
				DB.CreateParameter (cmd, "revision_id", revision_id);
				cmd.ExecuteNonQuery ();
			}
		}

		public void DeleteAllWork (int lane_id, int host_id)
		{
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "DELETE FROM Work WHERE lane_id = @lane_id AND host_id = @host_id;";
				DB.CreateParameter (cmd, "lane_id", lane_id);
				DB.CreateParameter (cmd, "host_id", host_id);
				cmd.ExecuteNonQuery ();
			}
			//TODO: Directory.Delete(Configuration.GetDataRevisionDir(lane, revision), true);
		}

		public void DeleteWork (int lane_id, int revision_id, int host_id)
		{
			using (IDbCommand cmd = Connection.CreateCommand ()) {
//				cmd.CommandText = "DELETE FROM Work WHERE lane_id = @lane_id AND revision_id = @revision_id AND host_id = @host_id;";
				cmd.CommandText = @"
DELETE FROM Work 
WHERE Work.revisionwork_id = 
	(SELECT id 
	 FROM RevisionWork 
	 WHERE		lane_id = @lane_id 
			AND revision_id = @revision_id 
			AND host_id = @host_id
	);";
				DB.CreateParameter (cmd, "lane_id", lane_id);
				DB.CreateParameter (cmd, "revision_id", revision_id);
				DB.CreateParameter (cmd, "host_id", host_id);
				cmd.ExecuteNonQuery ();
			}
			//TODO: Directory.Delete(Configuration.GetDataRevisionDir(lane, revision), true);
		}

		public DBRevision GetRevision (string lane, int revision)
		{
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * from revisions where lane = @lane AND revision = @revision";
				DB.CreateParameter (cmd, "lane", lane);
				DB.CreateParameter (cmd, "revision", revision.ToString ());
				using (IDataReader reader = cmd.ExecuteReader ()) {
					if (!reader.Read ())
						return null;
					if (reader.IsDBNull (0))
						return null;

					DBRevision rev = new DBRevision ();
					rev.Load (reader);
					return rev;
				}
			}
		}

		public int GetLastRevision (string lane)
		{
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				DBLane l = LookupLane (lane);
				cmd.CommandText = "SELECT max (CAST (revision AS int)) FROM Revision WHERE lane_id = @lane_id";
				DB.CreateParameter (cmd, "lane_id", l.id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					if (!reader.Read ())
						return 0;
					if (reader.IsDBNull (0))
						return 0;

					return reader.GetInt32 (0);
				}
			}
		}

		public List<DBWorkView> GetAllWork (DBLane lane, DBHost host)
		{
			List<DBWorkView> result = new List<DBWorkView> ();

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * FROM WorkView WHERE lane_id = @lane_id AND host_id = @host_id ORDER BY revision DESC, sequence ASC, lane DESC";
				DB.CreateParameter (cmd, "lane_id", lane.id);
				DB.CreateParameter (cmd, "host_id", host.id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ())
						result.Add (new DBWorkView (reader));
				}
			}
			return result;
		}

		public List<DBWorkView2> GetWork (DBRevisionWork revisionwork)
		{
			List<DBWorkView2> result = new List<DBWorkView2> ();

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * FROM WorkView2 WHERE revisionwork_id = @revisionwork_id ORDER BY sequence";
				DB.CreateParameter (cmd, "revisionwork_id", revisionwork.id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ())
						result.Add (new DBWorkView2 (reader));
				}
			}
			return result;
		}

		public bool HasWork (int lane_id, int revision_id, int host_id)
		{
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT Count (*) FROM Work WHERE lane_id = @lane_id AND revision_id = @revision_id AND host_id = @host_id";
				DB.CreateParameter (cmd, "lane_id", lane_id);
				DB.CreateParameter (cmd, "revision_id", revision_id);
				DB.CreateParameter (cmd, "host_id", host_id);
				return (int) cmd.ExecuteScalar () != 0;
			}
		}

		public DBWork GetNextStep (string lane)
		{
			DBWork result = null;

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * FROM steps WHERE lane = @lane AND (state = 0 OR state = 1) ORDER BY revision DESC, sequence LIMIT 1";
				DB.CreateParameter (cmd, "lane", lane);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					while (reader.Read ()) {
						if (result != null)
							throw new Exception ("Got more than one step");
						result = new DBWork ();
						result.Load (reader);
					}
				}
			}

			return result;
		}

		public DBHostLane GetHostLane (int host_id, int lane_id)
		{
			DBHostLane result;
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = "SELECT * FROM HostLane WHERE lane_id = @lane_id AND host_id = @host_id;";
				DB.CreateParameter (cmd, "host_id", host_id);
				DB.CreateParameter (cmd, "lane_id", lane_id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					if (!reader.Read ())
						return null;
					result = new DBHostLane (reader);
					if (reader.Read ())
						throw new Exception (string.Format ("Found more than one HostLane with host_id {0} and lane_id {1}", host_id, lane_id));
				}
			}
			return result;
		}

		/// <summary>
		/// Checks if the specified RevisionWork is the latest.
		/// </summary>
		/// <param name="current"></param>
		/// <returns></returns>
		public bool IsLatestRevisionWork (DBRevisionWork current)
		{
			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = @"
SELECT 
	RevisionWork.id
FROM 
	RevisionWork
INNER JOIN 
	Revision ON RevisionWork.revision_id = Revision.id
WHERE 	
	    lock_expires < now () AND
	    RevisionWork.host_id = @host_id 
	AND RevisionWork.lane_id = @lane_id
	AND RevisionWork.completed = false
ORDER BY Revision.date DESC
LIMIT 1
;";
				DB.CreateParameter (cmd, "host_id", current.host_id);
				DB.CreateParameter (cmd, "lane_id", current.lane_id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					if (!reader.Read ()) {
						Logger.Log ("IsLatestRevisionWork: No result.");
						return true;
					}

					if (reader.GetInt32 (0) <= current.id)
						return true;

					Logger.Log ("IsLatestRevisionWork: Latest id: {0}, current id: {1}", reader.GetInt32 (0), current.id);
					return false;
				}
			}
		}

		/// <summary>
		/// Will return a locked revision work.
		/// </summary>
		/// <param name="lane"></param>
		/// <param name="host"></param>
		/// <returns></returns>
		public DBRevisionWork GetRevisionWork (DBLane lane, DBHost host, DBHost workhost)
		{
			DBRevisionWork result = null;

			using (IDbCommand cmd = Connection.CreateCommand ()) {
				cmd.CommandText = @"
SELECT 
	RevisionWork.*
FROM 
	RevisionWork
INNER JOIN 
	Revision ON RevisionWork.revision_id = Revision.id
WHERE 
        RevisionWork.host_id = @host_id 
	AND (RevisionWork.workhost_id = @workhost_id OR RevisionWork.workhost_id IS NULL)
	AND RevisionWork.lane_id = @lane_id
	AND RevisionWork.completed = false
ORDER BY Revision.date DESC
LIMIT 1
;";
				DB.CreateParameter (cmd, "host_id", host.id);
				DB.CreateParameter (cmd, "lane_id", lane.id);
				DB.CreateParameter (cmd, "workhost_id", workhost.id);
				using (IDataReader reader = cmd.ExecuteReader ()) {
					if (reader.Read ())
						result = new DBRevisionWork (reader);
				}
			}

			return result;
		}

		/// <summary>
		/// The current date/time in the database.
		/// This is used to minimize date/time differences between 
		/// </summary>
		public DateTime Now
		{
			get
			{
				return DateTime.Now.Add (db_time_difference);
			}
		}
	}
}