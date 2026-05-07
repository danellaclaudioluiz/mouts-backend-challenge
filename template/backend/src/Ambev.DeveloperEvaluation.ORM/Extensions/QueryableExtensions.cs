using System.Linq.Expressions;
using System.Reflection;

namespace Ambev.DeveloperEvaluation.ORM.Extensions;

/// <summary>
/// IQueryable helpers used by repositories.
/// </summary>
public static class QueryableExtensions
{
    /// <summary>
    /// Applies an ordering expression like <c>"saleDate desc, totalAmount asc"</c>
    /// to an IQueryable. Property names are matched case-insensitively against
    /// the entity's public properties; unknown fields are silently ignored so
    /// a malicious caller cannot break the query, and an empty/null expression
    /// leaves the source untouched.
    /// </summary>
    public static IQueryable<T> OrderByDynamic<T>(this IQueryable<T> source, string? order)
    {
        if (string.IsNullOrWhiteSpace(order))
            return source;

        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        IQueryable<T> current = source;
        var first = true;

        foreach (var raw in order.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var propertyName = parts[0];
            var descending = parts.Length > 1 &&
                parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);

            var property = properties.FirstOrDefault(p =>
                p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            if (property is null) continue;

            var parameter = Expression.Parameter(typeof(T), "x");
            var access = Expression.MakeMemberAccess(parameter, property);
            var lambda = Expression.Lambda(access, parameter);

            var methodName = first
                ? (descending ? "OrderByDescending" : "OrderBy")
                : (descending ? "ThenByDescending" : "ThenBy");

            var call = Expression.Call(
                typeof(Queryable),
                methodName,
                new[] { typeof(T), property.PropertyType },
                current.Expression,
                Expression.Quote(lambda));

            current = current.Provider.CreateQuery<T>(call);
            first = false;
        }

        return current;
    }
}
