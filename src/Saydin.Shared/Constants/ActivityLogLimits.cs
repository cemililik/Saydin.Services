namespace Saydin.Shared.Constants;

/// <summary>
/// LOGR-007: activity_logs kolon kapasiteleri tek source-of-truth. Önceki sürümde
/// (a) <c>ActivityLogBuilder</c>'in Truncate sabitleri, (b) <c>ActivityLogConfiguration</c>
/// HasMaxLength çağrıları ve (c) 008/009 migration kolon tipleri üç ayrı yerde
/// hard-coded'du. Bu sınıftaki değerler EF Configuration + Builder + 011 migration
/// `chk_activity_data_size` (10000 byte) ile birebir uyumludur. Bir kapasite
/// değişimi: bu sabitleri güncelle + migration yaz.
/// </summary>
public static class ActivityLogLimits
{
    public const int DeviceIdMaxLength   = 200;
    public const int ActionMaxLength     = 30;
    public const int CountryMaxLength    = 2;
    public const int CityMaxLength       = 100;
    public const int DeviceOsMaxLength   = 30;
    public const int OsVersionMaxLength  = 100;
    public const int AppVersionMaxLength = 50;
    public const int ErrorCodeMaxLength  = 50;

    /// <summary>
    /// `data` JSONB için pg_column_size eşiği. INFR-009: `pg_column_size` TOAST-
    /// uncompressed binary boyutu döner; 10000 byte makul üst sınır.
    /// </summary>
    public const int DataMaxBytes = 10_000;
}
