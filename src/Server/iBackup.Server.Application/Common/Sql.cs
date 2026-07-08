using System.Data;
using Microsoft.Data.SqlClient;

namespace iBackup.Server.Application.Common;

/// <summary>Small ADO.NET helpers shared by all slices. Every value goes through a parameter.</summary>
public static class Sql
{
    public static SqlCommand Command(SqlConnection connection, string sql, SqlTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
        return command;
    }

    public static SqlCommand StoredProcedure(SqlConnection connection, string name, SqlTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = name;
        command.CommandType = CommandType.StoredProcedure;
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
        return command;
    }

    public static SqlCommand With(this SqlCommand command, string name, object? value, SqlDbType? type = null)
    {
        var parameter = command.Parameters.Add(name, type ?? Infer(value));
        parameter.Value = value ?? DBNull.Value;
        return command;
    }

    private static SqlDbType Infer(object? value) => value switch
    {
        null => SqlDbType.NVarChar,
        string => SqlDbType.NVarChar,
        Guid => SqlDbType.UniqueIdentifier,
        int => SqlDbType.Int,
        long => SqlDbType.BigInt,
        bool => SqlDbType.Bit,
        byte => SqlDbType.TinyInt,
        DateTime => SqlDbType.DateTime2,
        byte[] => SqlDbType.VarBinary,
        _ => SqlDbType.NVarChar
    };

    public static string? GetStringOrNull(this SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static DateTime? GetDateTimeOrNull(this SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    public static Guid? GetGuidOrNull(this SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    public static long? GetInt64OrNull(this SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static int? GetInt32OrNull(this SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static byte? GetByteOrNull(this SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetByte(ordinal);
}
