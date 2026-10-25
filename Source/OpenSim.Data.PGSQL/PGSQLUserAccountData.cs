using System.Data;
using OpenMetaverse;
using System.Text;
using Npgsql;
using System.Reflection;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL;

public class PGSQLUserAccountData : PGSQLGenericTableHandler<UserAccountData>,IUserAccountData
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public PGSQLUserAccountData(string connectionString, string realm) :
        base(connectionString, realm, "UserAccount")
    {
    }

    /*
    private string m_Realm;
    private List<string> m_ColumnNames = null;
    private PGSQLManager m_database;

    public PGSQLUserAccountData(string connectionString, string realm) :
        base(connectionString, realm, "UserAccount")
    {
        m_Realm = realm;
        m_connectionString = connectionString;
        m_database = new PGSQLManager(connectionString);

        using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
        {
            conn.Open();
            Migration m = new Migration(conn, GetType().Assembly, "UserAccount");
            m.Update();
        }
    }
    */
    /*
    public List<UserAccountData> Query(UUID principalID, UUID scopeID, string query)
    {
        return null;
    }
    */
    /*
    public override UserAccountData[] Get(string[] fields, string[] keys)
    {
        UserAccountData[] retUA = base.Get(fields,keys);

        if (retUA.Length > 0)
        {
            Dictionary<string, string> data = retUA[0].Data;
            Dictionary<string, string> data2 = new Dictionary<string, string>();

            foreach (KeyValuePair<string,string> chave in data)
            {
                string s2 = chave.Key;

                data2[s2] = chave.Value;

                if (!m_FieldTypes.ContainsKey(chave.Key))
                {
                    string tipo = "";
                    m_FieldTypes.TryGetValue(chave.Key, out tipo);
                    m_FieldTypes.Add(s2, tipo);
                }
            }
            foreach (KeyValuePair<string, string> chave in data2)
            {
                if (!retUA[0].Data.ContainsKey(chave.Key))
                    retUA[0].Data.Add(chave.Key, chave.Value);
            }
        }

        return retUA;
    }
    */
    public UserAccountData Get(UUID principalID, UUID scopeID)
    {
        UserAccountData ret = new UserAccountData();
        ret.Data = new Dictionary<string, string>();

        string sql = string.Format(@"select * from {0} where ""PrincipalID"" = :PrincipalID", m_Realm);
        if (scopeID != UUID.Zero)
            sql += @" and ""ScopeID"" = :ScopeID";

        using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
        using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.Add(m_database.CreateParameter("PrincipalID", principalID));
            cmd.Parameters.Add(m_database.CreateParameter("ScopeID", scopeID));

            conn.Open();
            using (NpgsqlDataReader result = cmd.ExecuteReader())
            {
                if (result.Read())
                {
                    ret.PrincipalID = principalID;
                    UUID scope;
                    UUID.TryParse(result["scopeid"].ToString(), out scope);
                    ret.ScopeID = scope;

                    if (m_ColumnNames == null)
                    {
                        m_ColumnNames = new List<string>();

                        DataTable schemaTable = result.GetSchemaTable();
                        foreach (DataRow row in schemaTable.Rows)
                            m_ColumnNames.Add(row["ColumnName"].ToString());
                    }

                    foreach (string s in m_ColumnNames)
                    {
                        string s2 = s;
                        if (s2 == "uuid")
                            continue;
                        if (s2 == "scopeid")
                            continue;

                        ret.Data[s] = result[s].ToString();
                    }
                    return ret;
                }
            }
        }
        return null;
    }

    public override bool Store(UserAccountData data)
    {
        return base.Store(data);
    }


    public bool Store(UserAccountData data, UUID principalID, string token)
    {
        return false;
    }


    public bool SetDataItem(UUID principalID, string item, string value)
    {
        string sql = string.Format(@"update {0} set {1} = :{1} where ""UUID"" = :UUID", m_Realm, item);
        using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
        using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
        {
            if (m_FieldTypes.ContainsKey(item))
                cmd.Parameters.Add(m_database.CreateParameter("\"" + item + "\"", value, m_FieldTypes[item]));
            else
                cmd.Parameters.Add(m_database.CreateParameter("\"" + item + "\"", value));

            cmd.Parameters.Add(m_database.CreateParameter("UUID", principalID));
            conn.Open();

            if (cmd.ExecuteNonQuery() > 0)
                return true;
        }
        return false;
    }
    /*
    public UserAccountData[] Get(string[] keys, string[] vals)
    {
        return null;
    }
    */

    public UserAccountData[] GetUsers(UUID scopeID, string query)
    {
        string[] words = query.Split();

        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length < 3)
            {
                if (i != words.Length - 1)
                {
                    Array.Copy(words, i + 1, words, i, words.Length - i - 1);
                    Array.Resize(ref words, words.Length - 1);
                }
            }
        }

        if (words.Length == 0)
            return new UserAccountData[0];

        if (words.Length > 2)
            return new UserAccountData[0];

        string sql = "";
        using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
        using (NpgsqlCommand cmd = new NpgsqlCommand())
        {
            if (words.Length == 1)
            {
                sql = String.Format(@"select * from {0} where (""ScopeID""=:ScopeID or ""ScopeID""=:UUIDZero) and (LOWER(""FirstName"") like LOWER(:search) or LOWER(""LastName"") like LOWER(:search))", m_Realm);
                cmd.Parameters.Add(m_database.CreateParameter("ScopeID", scopeID));
                cmd.Parameters.Add (m_database.CreateParameter("UUIDZero", UUID.Zero));
                cmd.Parameters.Add(m_database.CreateParameter("search", "%" + words[0] + "%"));
            }
            else
            {
                sql = String.Format(@"select * from {0} where (""ScopeID""=:ScopeID or ""ScopeID""=:UUIDZero) and (LOWER(""FirstName"") like LOWER(:searchFirst) or LOWER(""LastName"") like LOWER(:searchLast))", m_Realm);
                cmd.Parameters.Add(m_database.CreateParameter("searchFirst", "%" + words[0] + "%"));
                cmd.Parameters.Add(m_database.CreateParameter("searchLast", "%" + words[1] + "%"));
                cmd.Parameters.Add (m_database.CreateParameter("UUIDZero", UUID.Zero));
                cmd.Parameters.Add(m_database.CreateParameter("ScopeID", scopeID));
            }
            cmd.Connection = conn;
            cmd.CommandText = sql;
            conn.Open();
            return DoQuery(cmd);
        }
    }

    public UserAccountData[] GetUsersWhere(UUID scopeID, string where)
    {
        using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
        using (NpgsqlCommand cmd = new NpgsqlCommand())
        {
            // Fix case sensitivity for PostgreSQL column names
            where = where.Replace("PrincipalID", "\"PrincipalID\"")
                        .Replace("ScopeID", "\"ScopeID\"")
                        .Replace("FirstName", "\"FirstName\"")
                        .Replace("LastName", "\"LastName\"");
                        
            if (!scopeID.IsZero())
            {
                where = "(\"ScopeID\"=:ScopeID) and (" + where + ")";
                cmd.Parameters.Add(m_database.CreateParameter("ScopeID", scopeID));
            }

            cmd.CommandText = String.Format("select * from {0} where " + where, m_Realm);
            cmd.Connection = conn;
            
            conn.Open();
            return DoQuery(cmd);
        }
    }

    public override UserAccountData[] Get(string[] fields, string[] values)
    {
        return base.Get(fields, values);
    }

    public override bool Delete(string field, string val)
    {
        return base.Delete(field, val);
    }
}