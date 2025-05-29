Imports System.Security.Cryptography
Imports System.Text
Imports Org.BouncyCastle.Asn1.X9
Imports Org.BouncyCastle.Crypto
Imports Org.BouncyCastle.Crypto.EC
Imports Org.BouncyCastle.Crypto.Parameters
Imports Org.BouncyCastle.Security
Imports Org.BouncyCastle.Math.EC

Public Class Wallet

    Private Shared ReadOnly curveName As String = "secp256k1"
    Private Shared ReadOnly _curveParams As X9ECParameters = ECNamedCurveTable.GetByName(curveName)
    Private Shared ReadOnly _domainParams As ECDomainParameters = New ECDomainParameters(_curveParams.Curve, _curveParams.G, _curveParams.N, _curveParams.H)

    Public Shared Function VerifySignature(publicKeyBase64 As String, signatureBase64 As String, message As String) As Boolean
        Try
            Dim publicKeyBytes As Byte() = Convert.FromBase64String(publicKeyBase64)
            Dim q As Org.BouncyCastle.Math.EC.ECPoint = _domainParams.Curve.DecodePoint(publicKeyBytes)
            Dim keyParameters As New ECPublicKeyParameters(q, _domainParams)

            Dim signer As ISigner = SignerUtilities.GetSigner("SHA-256withECDSA")
            signer.Init(False, keyParameters)

            Dim messageBytes As Byte() = Encoding.UTF8.GetBytes(message)
            signer.BlockUpdate(messageBytes, 0, messageBytes.Length)

            Dim signatureBytes As Byte() = Convert.FromBase64String(signatureBase64)
            Return signer.VerifySignature(signatureBytes)

        Catch exAsn As Org.BouncyCastle.Asn1.Asn1Exception
            Console.WriteLine($"ASN.1 parsing error during signature verification (possibly malformed signature): {exAsn.Message}")
            Return False
        Catch ex As Exception
            Console.WriteLine($"Error verifying signature: {ex.Message} for pubkey: {publicKeyBase64.Substring(0, Math.Min(10, publicKeyBase64.Length))}..., message: {message}")
            Return False
        End Try
    End Function

    Public Shared Function IsValidPublicKey(publicKeyBase64 As String) As Boolean
        Try
            Dim publicKeyBytes As Byte() = Convert.FromBase64String(publicKeyBase64)
            Dim point As Org.BouncyCastle.Math.EC.ECPoint = _domainParams.Curve.DecodePoint(publicKeyBytes)
            Return point IsNot Nothing AndAlso Not point.IsInfinity AndAlso point.IsValid()
        Catch ex As Exception
            Console.WriteLine($"Public key validation error: {ex.Message} for key starting with {publicKeyBase64.Substring(0, Math.Min(10, publicKeyBase64.Length))}...")
            Return False
        End Try
    End Function

    Public Shared Function CalculateSHA256Hash(input As String) As String
        Using sha256 As SHA256 = SHA256.Create()
            Dim inputBytes As Byte() = Encoding.UTF8.GetBytes(input)
            Dim hashBytes As Byte() = sha256.ComputeHash(inputBytes)
            Return Convert.ToHexString(hashBytes).ToLower()
        End Using
    End Function

End Class