// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "pal_types.h"
#include "pal_compiler.h"
#include "opensslshim.h"
#include "pal_atomic.h"

struct EvpPKeyExtraHandle_st
{
    atomic_int refCount;
    OSSL_LIB_CTX* libCtx;
    OSSL_PROVIDER* prov;
};

typedef struct EvpPKeyExtraHandle_st EvpPKeyExtraHandle;

/*
Shims the EVP_PKEY_new method.

Returns the new EVP_PKEY instance.
*/
PALEXPORT EVP_PKEY* CryptoNative_EvpPkeyCreate(void);

/*
Cleans up and deletes a EVP_PKEY instance.

Implemented by calling EVP_PKEY_free.

No-op if pkey is null.
The given EVP_PKEY pointer is invalid after this call.
Always succeeds.
*/
PALEXPORT void CryptoNative_EvpPkeyDestroy(EVP_PKEY* pkey, void* extraHandle);

/*
Returns the cryptographic length of the cryptosystem to which the key belongs, in bits.
*/
PALEXPORT int32_t CryptoNative_EvpPKeyBits(EVP_PKEY* pkey);

/*
Used by System.Security.Cryptography.X509Certificates' OpenSslX509CertificateReader when
duplicating a private key context as part of duplicating the Pal object.

Returns the number (as of this call) of references to the EVP_PKEY. Anything less than
2 is an error, because the key is already in the process of being freed.
*/
PALEXPORT int32_t CryptoNative_UpRefEvpPkey(EVP_PKEY* pkey, void* extraHandle);

/*
Returns one of the following 4 values for the given EVP_PKEY:
    0 - unknown
    EVP_PKEY_RSA - RSA
    EVP_PKEY_EC - EC
    EVP_PKEY_DSA - DSA
*/
PALEXPORT int32_t CryptoNative_EvpPKeyType(EVP_PKEY* key);

/*
Decodes an X.509 SubjectPublicKeyInfo into an EVP_PKEY*, verifying the interpreted algorithm type.

Requires a non-null buf, and len > 0.
*/
PALEXPORT EVP_PKEY* CryptoNative_DecodeSubjectPublicKeyInfo(const uint8_t* buf, int32_t len, int32_t algId);

/*
Decodes an Pkcs8PrivateKeyInfo into an EVP_PKEY*, verifying the interpreted algorithm type.

Requires a non-null buf, and len > 0.
*/
PALEXPORT EVP_PKEY* CryptoNative_DecodePkcs8PrivateKey(const uint8_t* buf, int32_t len, int32_t algId);

/*
Gets the number of bytes rqeuired to encode an EVP_PKEY* as a Pkcs8PrivateKeyInfo.

On success, 1 is returned and p8size contains the size of the Pkcs8PrivateKeyInfo.
On failure, -1 is used to indicate the openssl error queue contains the error.
On failure, -2 is used to indcate that the supplied EVP_PKEY* is possibly missing a private key.
*/
PALEXPORT int32_t CryptoNative_GetPkcs8PrivateKeySize(EVP_PKEY* pkey, int32_t* p8size);

/*
Encodes the EVP_PKEY* as a Pkcs8PrivateKeyInfo, writing the encoded value to buf.

buf must be big enough, or an out of bounds write may occur.

Returns the number of bytes written.
*/
PALEXPORT int32_t CryptoNative_EncodePkcs8PrivateKey(EVP_PKEY* pkey, uint8_t* buf);

/*
Reports the number of bytes rqeuired to encode an EVP_PKEY* as an X.509 SubjectPublicKeyInfo, or a negative value on error.
*/
PALEXPORT int32_t CryptoNative_GetSubjectPublicKeyInfoSize(EVP_PKEY* pkey);

/*
Encodes the EVP_PKEY* as an X.509 SubjectPublicKeyInfo, writing the encoded value to buf.

buf must be big enough, or an out of bounds write may occur.

Returns the number of bytes written.
*/
PALEXPORT int32_t CryptoNative_EncodeSubjectPublicKeyInfo(EVP_PKEY* pkey, uint8_t* buf);

/*
Load a named key, via ENGINE_load_private_key, from the named engine.

Returns a valid EVP_PKEY* on success, NULL on failure.
haveEngine is 1 if OpenSSL ENGINE's are supported, otherwise 0.
*/
PALEXPORT EVP_PKEY* CryptoNative_LoadPrivateKeyFromEngine(const char* engineName, const char* keyName, int32_t* haveEngine);

/*
Load a named key, via ENGINE_load_public_key, from the named engine.

Returns a valid EVP_PKEY* on success, NULL on failure.
*haveEngine is 1 if OpenSSL ENGINE's are supported, otherwise 0.
*/
PALEXPORT EVP_PKEY* CryptoNative_LoadPublicKeyFromEngine(const char* engineName, const char* keyName, int32_t* haveEngine);

/*
Load a key by URI from a specified OSSL_PROVIDER.

Returns a valid EVP_PKEY* on success, NULL on failure.
On success extraHandle may be non-null value which we need to keep alive
until the EVP_PKEY is destroyed.

*haveProvider is 1 if OpenSSL providers are supported, otherwise 0.
*/
PALEXPORT EVP_PKEY* CryptoNative_LoadKeyFromProvider(const char* providerName, const char* keyUri, void** extraHandle, int32_t* haveProvider);

/*
Loads a key using EVP_PKEY_fromdata_init and EVP_PKEY_fromdata.
*/
PALEXPORT EVP_PKEY* CryptoNative_EvpPKeyFromData(const char* algorithmName, uint8_t* key, int32_t keyLength, int32_t privateKey);

/*
It's a wrapper for EVP_PKEY_CTX_new_from_pkey and EVP_PKEY_CTX_new
which handles extraHandle.
*/
EVP_PKEY_CTX* EvpPKeyCtxCreateFromPKey(EVP_PKEY* pkey, void* extraHandle);

/*
Internal function to get the octet string parameter from the given EVP_PKEY.
*/
int32_t EvpPKeyGetKeyOctetStringParam(const EVP_PKEY* pKey,
                                      const char* name,
                                      uint8_t* destination,
                                      int32_t destinationLength);

/*
Internal function to determine if an EVP_PKEY has a given octet string property.
*/
int32_t EvpPKeyHasKeyOctetStringParam(const EVP_PKEY* pKey, const char* name);
