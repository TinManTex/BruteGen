using System;
using System.Collections.Generic;

namespace BruteGen {
    class HashFuncs {
        public delegate string HashFunction(string str);//tex makes testing different hash types a bit easier by outputing the hash as a string.

        //tex string hash function name to HashFunc
        //add to this if you add new HashFuncs
        //hash function name should be lowercase
        private static Dictionary<string, HashFunction> hashFuncs = new Dictionary<string, HashFunction> {
            {"strcode32", StrCode32Str},
            {"strcode64", StrCode64Str},
            {"pathfilenamecode32", PathFileNameCode32Str},
            {"pathfilenamecode64", PathFileNameCode64Str},//tex for want of a better name
            {"pathcode64", PathCode64Str},
            {"pathcode64gz", PathCode64GzStr},
            {"extensioncode64", ExtensionCode64Str },
        };

        public static HashFunction GetHashFuncByName(string funcName) {
            HashFunction hashFunc = null;
            try {
                hashFunc = hashFuncs[funcName.ToLower()];
            } catch (KeyNotFoundException) {
                hashFunc = null;
            }

            return hashFunc;
        }//GetHashFuncByName

        //Hashfuncs
        public static string StrCode32Str(string text) {
            var hash = (uint)Hashing.FoxEngine.StrCode(text);
            return hash.ToString();
        }
        public static string StrCode64Str(string text) {
            var hash = Hashing.FoxEngine.StrCode(text);
            return hash.ToString();
        }
        //TODO: verify output matches lua PathFileNameCode32 (it was missing in some cases? see mockfox pathfilename note?)
        public static string PathFileNameCode32Str(string text) {
            var hash = (uint)Hashing.FoxEngine.HashFileNameWithExtension(text);
            return hash.ToString();
        }
        public static string PathFileNameCode64Str(string text) {
            ulong hash = Hashing.FoxEngine.HashFileNameWithExtension(text);
            return hash.ToString();
        }
        //tex DEBUGNOW TODO name, this is more specific to gzstool dictionary implementation than a general Fox implementation?
        public static string PathCode64Str(string text) {
            ulong hash = Hashing.FoxEngine.HashFileName(text) & 0x3FFFFFFFFFFFF;
            return hash.ToString("x");
        }

        public static string PathCode64GzStr(string text) {
            ulong hash = Hashing.FoxEngine.StrCode(text);
            return hash.ToString("x");
        }

        public static string ExtensionCode64Str(string text) {
            ulong hash = Hashing.FoxEngine.HashFileExtension(text);
            return hash.ToString();
        }
    }//HashFuncs
}
