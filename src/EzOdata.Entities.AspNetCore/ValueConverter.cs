using System.Globalization;
using EzOdata.Core;
using EzOdata.Core.Query;

namespace EzOdata.Entities.AspNetCore;

/// <summary>Engine values (typed by EDM type) → entity property types.</summary>
internal static class ValueConverter
{
    public static object? To(object? value, Type target, string column)
    {
        var underlying = Nullable.GetUnderlyingType(target);
        if (value is null or DBNull)
        {
            if (target.IsValueType && underlying is null)
            {
                throw new ConnectorException(ErrorCodes.ValidationNotNullViolation, $"Property '{column}' cannot be null.");
            }

            return null;
        }

        var type = underlying ?? target;
        if (type.IsInstanceOfType(value)) return value;

        try
        {
            if (type.IsEnum)
            {
                return value is string s
                    ? Enum.Parse(type, s, ignoreCase: true)
                    : Enum.ToObject(type, Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture)!);
            }

            if (type == typeof(Guid))
            {
                return value switch
                {
                    string s => Guid.Parse(s),
                    byte[] b => new Guid(b),
                    _ => throw new InvalidCastException(),
                };
            }

            if (type == typeof(DateTime) && value is DateTimeOffset dto) return dto.Offset == TimeSpan.Zero ? DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Utc) : dto.UtcDateTime;
            if (type == typeof(DateTimeOffset) && value is DateTime dt) return new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt);
            if (type == typeof(DateTimeOffset) && value is string ds) return DateTimeOffset.Parse(ds, CultureInfo.InvariantCulture);
            if (type == typeof(DateTime) && value is string dts) return DateTime.Parse(dts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type == typeof(DateOnly) && value is DateTime d) return DateOnly.FromDateTime(d);
            if (type == typeof(TimeOnly) && value is TimeSpan ts) return TimeOnly.FromTimeSpan(ts);
            if (type == typeof(TimeSpan) && value is string tss) return TimeSpan.Parse(tss, CultureInfo.InvariantCulture);

            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new ConnectorException(ErrorCodes.ValidationInvalidValue,
                $"Value for '{column}' cannot be converted to {type.Name}.");
        }
    }
}
