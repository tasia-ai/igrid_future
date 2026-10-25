/* 
 * Copyright (c) Contributors, http://opensimulator.org/ 
 * See CONTRIBUTORS.TXT for a full list of copyright holders. 
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer. 
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in
 *       the documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
using Npgsql;
using OpenMetaverse;
using System.Text;
using System.Reflection;
using System.Data;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL;

public class PGSQLGenericTableHandler<T> : PGSqlFramework where T : class, new()
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    protected PGSQLManager m_database; //used for parameter type translation
    protected Dictionary<string, FieldInfo> m_Fields =
            new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);

    protected Dictionary<string, string> m_FieldTypes = new Dictionary<string, string>();

    protected List<string> m_ColumnNames = null;
    protected string m_Realm;
    protected FieldInfo m_DataField = null;

    protected virtual Assembly Assembly
    {
        get { return GetType().Assembly; }
    }

    public PGSQLGenericTableHandler(
        string connectionString, string realm, string storeName)
        : base(connectionString)
    {
        m_Realm = realm;

        m_connectionString = connectionString;

        if (storeName != String.Empty)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, GetType().Assembly, storeName);
                m.Update();
            }

        }
        m_database = new PGSQLManager(m_connectionString);

        Type t = typeof(T);
        FieldInfo[] fields = t.GetFields(BindingFlags.Public |
                                         BindingFlags.Instance |
                                         BindingFlags.DeclaredOnly);

        LoadFieldTypes();

        if (fields.Length == 0)
            return;

        foreach (FieldInfo f in fields)
        {
            if (f.Name != "Data")
                m_Fields[f.Name] = f;
            else
                m_DataField = f;
        }
    }

    private void LoadFieldTypes()
    {
        using var conn = new NpgsqlConnection(m_connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand("SELECT a.attname, format_type(a.atttypid, NULL) FROM pg_attribute a WHERE a.attrelid = to_regclass(@table) AND a.attnum > 0 AND NOT a.attisdropped", conn);
        cmd.Parameters.AddWithValue("table", m_Realm);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            m_FieldTypes[reader.GetString(0)] = reader.GetString(1);
        if (m_FieldTypes.Count == 0)
            throw new InvalidOperationException($"Storage table {m_Realm} does not exist after migration");
    }

    private NpgsqlParameter Parameter(string name, string column, object value)
    {
        if (!m_FieldTypes.TryGetValue(column, out string type))
            throw new ArgumentException($"Unknown storage column {column}");
        return m_database.CreateParameter(name, value, type);
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private string Conditions(NpgsqlCommand cmd, string[] fields, string[] values)
    {
        if (fields.Length != values.Length || fields.Length == 0)
            throw new ArgumentException("Storage filters must contain matching fields and values");
        var terms = new List<string>();
        for (int i = 0; i < fields.Length; i++)
        {
            string name = "p" + i;
            cmd.Parameters.Add(Parameter(name, fields[i], values[i]));
            terms.Add(Quote(fields[i]) + " = @" + name);
        }
        return string.Join(" AND ", terms);
    }

    public virtual bool Store(T data)
    {
        var values = m_Fields.ToDictionary(pair => pair.Key, pair => pair.Value.GetValue(data));
        if (m_DataField?.GetValue(data) is Dictionary<string, string> extra)
            foreach (var pair in extra)
                values[pair.Key] = pair.Value;
        using var conn = new NpgsqlConnection(m_connectionString);
        conn.Open();
        // Use one unique index (prefer the primary key), never combine unrelated keys.
        using var keyCmd = new NpgsqlCommand(@"SELECT array_agg(a.attname ORDER BY k.ordinality)
            FROM pg_index i CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum, ordinality)
            JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
            WHERE i.indrelid=to_regclass(@table) AND i.indisunique AND i.indpred IS NULL
              AND i.indexprs IS NULL AND k.ordinality <= i.indnkeyatts
            GROUP BY i.indexrelid, i.indisprimary ORDER BY i.indisprimary DESC, i.indexrelid LIMIT 1", conn);
        keyCmd.Parameters.AddWithValue("table", m_Realm);
        string[] keys = keyCmd.ExecuteScalar() as string[];
        using var cmd = new NpgsqlCommand { Connection = conn };
        var names = values.Keys.ToArray();
        for (int i = 0; i < names.Length; i++)
            cmd.Parameters.Add(Parameter("p" + i, names[i], values[names[i]]));
        string conflict = keys == null ? "" : " ON CONFLICT (" + string.Join(",", keys.Select(Quote)) + ") DO UPDATE SET " +
            string.Join(",", names.Select(n => Quote(n) + " = EXCLUDED." + Quote(n)));
        cmd.CommandText = "INSERT INTO " + m_Realm + " (" + string.Join(",", names.Select(Quote)) + ") VALUES (" +
            string.Join(",", names.Select((_, i) => "@p" + i)) + ")" + conflict;
        return cmd.ExecuteNonQuery() > 0;
    }

    public virtual T[] Get(string[] ids, string[] values)
    {
        using var cmd = new NpgsqlCommand();
        cmd.CommandText = "SELECT * FROM " + m_Realm + " WHERE " + Conditions(cmd, ids, values);
        return DoQuery(cmd);
    }

    public virtual T[] Get(string field, string value)
    {
        return Get(new string[] { field }, new string[] { value });
    }

    public virtual T[] Get(string whereClause)
    {
        using (NpgsqlCommand cmd = new NpgsqlCommand())
        {
            cmd.CommandText = String.Format("select * from {0} where {1}", m_Realm, whereClause);
            return DoQuery(cmd);
        }
    }

    public virtual int GetCount(string whereClause)
    {
        using (NpgsqlCommand cmd = new NpgsqlCommand())
        {
            cmd.CommandText = String.Format("select count(*) from {0} where {1}", m_Realm, whereClause);
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                cmd.Connection = conn;
                conn.Open();
                object result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    return Convert.ToInt32(result);
            }
        }
        return 0;
    }

    public virtual int GetCount(string field, string value)
    {
        return GetCount(new string[] { field }, new string[] { value });
    }

    public virtual int GetCount(string[] fields, string[] values)
    {
        using (NpgsqlCommand cmd = new NpgsqlCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM " + m_Realm + " WHERE " + Conditions(cmd, fields, values);
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                cmd.Connection = conn;
                conn.Open();
                object result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    return Convert.ToInt32(result);
            }
        }
        return 0;
    }

    public virtual bool Delete(string id1, string id2)
    {
        return Delete(new[] { id1 }, new[] { id2 });
    }

    public virtual bool Delete(string[] ids, string[] values)
    {
        using var cmd = new NpgsqlCommand();
        cmd.CommandText = "DELETE FROM " + m_Realm + " WHERE " + Conditions(cmd, ids, values);
        return ExecuteNonQuery(cmd) > 0;
    }

    protected T[] DoQuery(NpgsqlCommand cmd)
    {
        List<T> results = new List<T>();
        using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
        {
            cmd.Connection = conn;
            conn.Open();
            using (NpgsqlDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    T item = new T();
                    Type itemType = typeof(T);
                    var fields = itemType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    Dictionary<string, string> dataDict = null;
                    FieldInfo dataField = null;

                    foreach (var field in fields)
                    {
                        if (field.FieldType == typeof(Dictionary<string, string>))
                        {
                            dataField = field;
                            dataDict = new Dictionary<string, string>();
                            field.SetValue(item, dataDict);
                            continue;
                        }

                        string colName = field.Name;
                        object value = null;

                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            if (string.Equals(reader.GetName(i), colName, System.StringComparison.OrdinalIgnoreCase))
                            {
                                if (!reader.IsDBNull(i))
                                {
                                    value = reader.GetValue(i);
                                }
                                break;
                            }
                        }

                        if (value != null && value != DBNull.Value)
                        {
                            try
                            {
                                field.SetValue(item, field.FieldType == typeof(UUID)
                                    ? UUID.Parse(value.ToString())
                                    : Convert.ChangeType(value, field.FieldType, System.Globalization.CultureInfo.InvariantCulture));
                            }
                            catch (Exception exception)
                            {
                                throw new DataException($"Cannot map {m_Realm}.{colName} to {field.FieldType}", exception);
                            }
                        }
                    }

                    // Add remaining columns to Data dictionary
                    if (dataDict != null)
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            string colName = reader.GetName(i);
                            bool alreadyMapped = false;
                            foreach (var field in fields)
                            {
                                if (string.Equals(field.Name, colName, System.StringComparison.OrdinalIgnoreCase))
                                {
                                    alreadyMapped = true;
                                    break;
                                }
                            }
                            if (!alreadyMapped && !reader.IsDBNull(i))
                            {
                                dataDict[colName] = reader.GetValue(i).ToString();
                            }
                        }
                    }

                    results.Add(item);
                }
            }
        }
        return results.ToArray();
    }
}