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

    protected string m_connectionString;
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
    }

    protected int ExecuteNonQuery(NpgsqlCommand cmd)
    {
        lock (m_dbLock)
        {
            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();
                cmd.Connection = dbcon;

                try
                {
                    return cmd.ExecuteNonQuery();
                }
                catch (Exception e)
                {
                    m_log.LogError(e, e.Message);
                    return 0;
                }
            }
        }
    }

    public virtual bool Store(T data)
    {
        return false;
    }

    public virtual T[] Get(string[] ids, string[] values)
    {
        return new T[0];
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
        List<string> conditions = new List<string>();
        for (int i = 0; i < fields.Length && i < values.Length; i++)
            conditions.Add(String.Format("\"{0}\" = :{1}", fields[i], "p" + i));
        string where = String.Join(" AND ", conditions.ToArray());
        if (where.Length == 0) where = "1";
        using (NpgsqlCommand cmd = new NpgsqlCommand())
        {
            cmd.CommandText = String.Format("select count(*) from {0} where {1}", m_Realm, where);
            for (int i = 0; i < values.Length; i++)
                cmd.Parameters.Add(new NpgsqlParameter("p" + i, values[i]));
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
        return false;
    }

    public virtual bool Delete(string[] ids, string[] values)
    {
        return false;
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
                                field.SetValue(item, Convert.ChangeType(value, field.FieldType));
                            }
                            catch
                            {
                                // Skip fields that can't be converted
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