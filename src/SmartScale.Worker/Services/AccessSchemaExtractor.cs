using System.Data;
using System.Data.OleDb;
using Microsoft.Extensions.Logging;

namespace SmartScale.Worker.Services;

// רשומות לייצוג הסכמה שחולצה מ-Access
public record ColumnInfo(string Name, string SqlType, bool IsNullable, int OrdinalPosition);
public record TableSchema(string TableName, List<ColumnInfo> Columns, List<string> PrimaryKeyColumns);

/// <summary>
/// מחלץ סכמת טבלאות מקובץ Access דרך OLEDB. ללא הגדרה ידנית של טבלאות.
/// </summary>
public class AccessSchemaExtractor
{
    private readonly ILogger<AccessSchemaExtractor> _logger;

    public AccessSchemaExtractor(ILogger<AccessSchemaExtractor> logger) => _logger = logger;

    public List<TableSchema> ExtractSchemas(string connectionString, string? tableFilter = null)
    {
        var result = new List<TableSchema>();

        using var conn = new OleDbConnection(connectionString);
        conn.Open();

        // שלב א: רשימת כל טבלאות המשתמש (לא טבלאות מערכת)
        var tablesSchema = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Tables,
            new object?[] { null, null, null, "TABLE" })!;

        foreach (DataRow tableRow in tablesSchema.Rows)
        {
            var tableName = tableRow["TABLE_NAME"].ToString()!;

            // דלג על טבלאות מערכת של Access
            if (tableName.StartsWith("MSys", StringComparison.OrdinalIgnoreCase) ||
                tableName.StartsWith("~"))
                continue;

            // דלג על טבלאות שלא בפילטר
            if (tableFilter != null && !tableName.Equals(tableFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            // שלב ב: עמודות הטבלה
            var colSchema = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Columns,
                new object?[] { null, null, tableName, null })!;

            var columns = new List<ColumnInfo>();
            foreach (DataRow colRow in colSchema.Rows)
            {
                var colName    = colRow["COLUMN_NAME"].ToString()!;
                var oleDbType  = (OleDbType)Convert.ToInt32(colRow["DATA_TYPE"]);
                var maxLength  = colRow["CHARACTER_MAXIMUM_LENGTH"] is DBNull
                    ? (int?)null
                    : (int?)Convert.ToInt64(colRow["CHARACTER_MAXIMUM_LENGTH"]);
                var isNullable = colRow["IS_NULLABLE"] is not DBNull && Convert.ToBoolean(colRow["IS_NULLABLE"]);
                var ordinal    = Convert.ToInt32(colRow["ORDINAL_POSITION"]);

                var sqlType = MapOleDbTypeToSqlType(oleDbType, maxLength, colName);
                columns.Add(new ColumnInfo(colName, sqlType, isNullable, ordinal));
            }
            columns = columns.OrderBy(c => c.OrdinalPosition).ToList();

            // שלב ג: מפתח ראשי
            var pkSchema = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Primary_Keys,
                new object?[] { null, null, tableName })!;

            var pkColumns = pkSchema.Rows
                .Cast<DataRow>()
                .OrderBy(r => Convert.ToInt32(r["ORDINAL"]))
                .Select(r => r["COLUMN_NAME"].ToString()!)
                .ToList();

            if (pkColumns.Count == 0)
            {
                var idCol = columns.FirstOrDefault(c => c.Name.Equals("id", StringComparison.OrdinalIgnoreCase));
                if (idCol != null)
                {
                    pkColumns.Add(idCol.Name);
                    _logger.LogInformation("טבלה [{Table}] ללא PK מוגדר — משתמש ב-[{Col}] כמפתח ראשי", tableName, idCol.Name);
                }
                else
                {
                    _logger.LogInformation("טבלה [{Table}] ללא PK ולא נמצאה עמודת id — Full Replace", tableName);
                }
            }

            result.Add(new TableSchema(tableName, columns, pkColumns));
        }

        return result;
    }

    private string MapOleDbTypeToSqlType(OleDbType type, int? maxLength, string colName)
    {
        switch (type)
        {
            // מחרוזות קצרות
            case OleDbType.Char:
            case OleDbType.WChar:
            case OleDbType.VarChar:
            case OleDbType.VarWChar:
            case OleDbType.BSTR:
                return "NVARCHAR(MAX)";

            // Memo / Long Text
            case OleDbType.LongVarChar:
            case OleDbType.LongVarWChar:
                return "NVARCHAR(MAX)";

            case OleDbType.Integer:
            case OleDbType.SmallInt:
            case OleDbType.UnsignedSmallInt:
                return "INT";

            case OleDbType.TinyInt:
            case OleDbType.UnsignedTinyInt:
                return "TINYINT";

            case OleDbType.BigInt:
            case OleDbType.UnsignedBigInt:
                return "BIGINT";

            case OleDbType.Boolean:
                return "BIT";

            case OleDbType.Single:
                return "REAL";

            case OleDbType.Double:
                return "FLOAT";

            case OleDbType.Currency:
                return "MONEY";

            case OleDbType.Date:
            case OleDbType.DBDate:
            case OleDbType.DBTime:
            case OleDbType.DBTimeStamp:
            case OleDbType.Filetime:
                return "DATETIME2";

            case OleDbType.Decimal:
            case OleDbType.Numeric:
                return "DECIMAL(18,4)";

            case OleDbType.Guid:
                return "UNIQUEIDENTIFIER";

            case OleDbType.Binary:
            case OleDbType.VarBinary:
            case OleDbType.LongVarBinary:
                return "NVARCHAR(MAX)";

            default:
                _logger.LogWarning("טיפוס לא מוכר {Type} לעמודה [{Col}] — משתמש ב-NVARCHAR(MAX)", type, colName);
                return "NVARCHAR(MAX)";
        }
    }
}
