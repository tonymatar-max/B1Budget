using B1Budget.Api.Domain;
using B1Budget.Api.Services.B1;

namespace B1Budget.Api.Services;

public class GatewayFactory(SecretProtector secrets)
{
    public IB1Gateway Create(Company c)
    {
        if (c.Mode == B1Mode.Mock) return new MockGateway(c.Id);
        if (string.IsNullOrWhiteSpace(c.CompanyDb) || string.IsNullOrWhiteSpace(c.PasswordEncrypted))
            throw new InvalidOperationException($"{c.Name}: Service Layer connection is not configured — set it up under Companies.");
        var sl = new ServiceLayerClient(new B1ConnectionInfo
        {
            BaseUrl = c.ServiceLayerUrl,
            CompanyDB = c.CompanyDb,
            UserName = c.UserName,
            Password = Decrypt(c),
            IgnoreSslErrors = c.IgnoreSslErrors,
        });
        return new ServiceLayerGateway(sl, c.FieldMapOverrideJson);
    }

    private string Decrypt(Company c)
    {
        try { return secrets.Unprotect(c.PasswordEncrypted!); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Keys are tied to the app's folder and data/keys — moving/redeploying the app without them invalidates stored passwords.
            throw new InvalidOperationException(
                $"{c.Name}: the stored Service Layer password can't be decrypted (the app or its data/keys folder was moved). Re-enter the password under Companies.");
        }
    }
}
