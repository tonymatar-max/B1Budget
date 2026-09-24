using Microsoft.AspNetCore.DataProtection;

namespace B1Budget.Api.Services;

/// <summary>Encrypts the Service Layer password at rest (ASP.NET DataProtection, keys under data/keys).</summary>
public class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _p = provider.CreateProtector("B1Budget.Secrets.v1");
    public string Protect(string plain) => _p.Protect(plain);
    public string Unprotect(string cipher) => _p.Unprotect(cipher);
}
