Imports System.ComponentModel.Design
Imports System.Security.Cryptography
Imports System.Text
Imports Org.BouncyCastle.Asn1.X9
Imports Org.BouncyCastle.Crypto
Imports Org.BouncyCastle.Crypto.EC
Imports Org.BouncyCastle.Crypto.Parameters
Imports Org.BouncyCastle.Security

Public Class Wallet

    Private Shared ReadOnly curve As X9ECParameters = ECNamedCurveTable.GetByName("secp256k1")
    Private Shared ReadOnly domainParams As ECDomainParameters = New ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H)

    Public Shared Function VerifySignature(publicKey As String, signature As String, transactionData As String) As Boolean
        Try
            ' Decode the public key
            Dim publicKeyBytes As Byte() = Convert.FromBase64String(publicKey)
            Dim keyParameters As New ECPublicKeyParameters(
                curve.Curve.DecodePoint(publicKeyBytes),
                domainParams
            )

            ' Create a SHA256withECDSA signer
            Dim signer As ISigner = SignerUtilities.GetSigner("SHA256withECDSA")

            ' Initialize the signer for verification
            signer.Init(False, keyParameters)

            ' Use the transactionData parameter directly (no need to reconstruct it)
            Dim bytes As Byte() = Encoding.UTF8.GetBytes(transactionData)

            ' Update the signer with the transaction data
            signer.BlockUpdate(bytes, 0, bytes.Length)

            ' Decode the signature
            Dim signatureBytes As Byte() = Convert.FromBase64String(signature)

            ' Verify the signature
            Return signer.VerifySignature(signatureBytes)

        Catch ex As Exception
            Console.WriteLine($"Error verifying signature: {ex.Message}")
            Return False
        End Try
    End Function

    Public Shared Function IsValidPublicKey(publicKeyString As String) As Boolean
        Try
            ' Decode the public key
            Dim publicKeyBytes As Byte() = Convert.FromBase64String(publicKeyString)

            ' Get the ECDomainParameters
            Dim ecP As X9ECParameters = CustomNamedCurves.GetByName("secp256k1")
            Dim parameters As ECDomainParameters = New ECDomainParameters(ecP.Curve, ecP.G, ecP.N, ecP.H, ecP.GetSeed())

            ' Check if the public key is a valid point on the curve
            Return parameters.Curve.DecodePoint(publicKeyBytes).IsValid()

        Catch ex As Exception
            Console.WriteLine($"Error validating public key: {ex.Message}")
            Return False
        End Try
    End Function

    Public Shared Function CalculateSHA256Hash(input As String) As String
        Using sha256 As SHA256 = SHA256.Create()
            Dim inputBytes As Byte() = System.Text.Encoding.UTF8.GetBytes(input)
            Dim hashBytes As Byte() = sha256.ComputeHash(inputBytes)
            Return Convert.ToHexString(hashBytes).ToLower()
        End Using
    End Function

End Class