using QRCoder;

namespace CodexProfileOverlay.Core.Services;

public static class SupportQrCodeService
{
    public static byte[] CreatePng(string methodId, string checkoutUrl, int pixelsPerModule = 8)
    {
        bool directAddress = methodId.Equals("direct_crypto", StringComparison.OrdinalIgnoreCase) &&
            checkoutUrl.Length is >= 24 and <= 128 &&
            checkoutUrl.All(character => char.IsLetterOrDigit(character) || character is 'x' or 'X');
        if (!directAddress && !CheckoutUrlValidator.TryValidateCheckout(methodId, checkoutUrl, out _))
        {
            throw new InvalidDataException("The checkout URL is not approved for this payment method.");
        }

        using QRCodeData data = QRCodeGenerator.GenerateQrCode(checkoutUrl, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        return code.GetGraphic(pixelsPerModule, drawQuietZones: true);
    }
}
