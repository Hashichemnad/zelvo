using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zelvo.Data.Localization;

/// <summary>Maps <see cref="LocalizedText"/> properties to <c>&lt;column&gt;_en</c> and <c>&lt;column&gt;_ar</c> columns.</summary>
public static class LocalizedTextModelBuilderExtensions
{
    /// <summary>Maps a bilingual property to two text columns of at most <paramref name="maxLength"/> characters.</summary>
    public static EntityTypeBuilder<TEntity> ConfigureLocalizedText<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, LocalizedText?>> property,
        string column,
        int? maxLength = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ComplexProperty(property, text =>
        {
            var en = text.Property(t => t.En).HasColumnName($"{column}_en");
            var ar = text.Property(t => t.Ar).HasColumnName($"{column}_ar");
            if (maxLength is { } length)
            {
                en.HasMaxLength(length);
                ar.HasMaxLength(length);
            }
        });
        return builder;
    }
}
