namespace SPVision.Hik;

/// <summary>
/// HCNetSDK error. <see cref="Code"/> is the value returned by NET_DVR_GetLastError().
/// Descriptions follow the NET_DVR_* error defines in incEn\HCNetSDK.h (V6.1.11.40).
/// </summary>
public sealed class HikException(uint code, string message) : Exception($"{message} (SDK {code}: {Describe(code)})")
{
    public uint Code { get; } = code;

    /// <summary>Captures NET_DVR_GetLastError() once and wraps it with context.</summary>
    public static HikException LastError(string context)
    {
        uint code = HikNative.NET_DVR_GetLastError();
        return new HikException(code, $"{context}: {Describe(code)}");
    }

    public static string Describe(uint code) => code switch
    {
        0 => "Başarılı",
        1 => "Kullanıcı adı veya şifre hatalı",
        2 => "Yetersiz yetki",
        3 => "SDK başlatılmamış",
        4 => "Kanal numarası hatalı",
        5 => "Cihazın izin verdiği maksimum istemci bağlantı sayısı aşıldı",
        6 => "SDK sürümü uyuşmuyor",
        7 => "Cihaza bağlanılamadı (adresi, portu ve ağı kontrol edin)",
        8 => "Cihaza veri gönderilemedi",
        9 => "Cihazdan veri alınamadı",
        10 => "Cihazdan veri alma zaman aşımına uğradı",
        11 => "Aktarılan veride hata var",
        12 => "API çağrı sırası hatalı",
        13 => "Bu işlem için yetki yok",
        14 => "İşlem zaman aşımına uğradı",
        17 => "Parametre hatası",
        23 => "Cihaz bu özelliği desteklemiyor",
        28 => "Cihaz kaynakları yetersiz",
        41 => "SDK kaynakları ayrılamadı",
        43 => "Tampon bellek yetersiz",
        44 => "Soket oluşturulamadı",
        47 => "Kullanıcı hesabı cihazda yok",
        51 => "Oynatıcı (PlayCtrl) hatası",
        64 => "PlayCtrl.dll yüklenemedi (uygulama klasöründeki SDK dosyalarını kontrol edin)",
        72 => "Soket bağlama hatası",
        73 => "Bağlantı koptu (ağ kesintisi)",
        153 => "Hesap geçici olarak kilitli (çok sayıda hatalı giriş)",
        _ => $"Bilinmeyen SDK hatası ({code})",
    };
}
