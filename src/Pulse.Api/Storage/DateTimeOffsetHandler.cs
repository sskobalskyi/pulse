using System.Data;
using Dapper;

namespace Pulse.Api.Storage;

/// <summary>Npgsql returns timestamptz as UTC DateTime; Dapper can't bind that to DateTimeOffset on its own.</summary>
public sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt.ToUniversalTime()),
        _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset"),
    };

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.DbType = DbType.DateTime;
        parameter.Value = value.UtcDateTime;
    }
}
