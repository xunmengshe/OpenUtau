using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using OpenUtau.Api;
using OpenUtau.Core.G2p;

namespace MonophonePhonemizer
{
    [Phonemizer("English Monophone Phonemizer", "EN MONO", language: "EN")]
    public class EnglishMonophonePhonemizer: MonophoneG2pPhonemizer
    {
        protected override string GetDictionaryName()=>"mono-en.yaml";
        protected override IG2p LoadBaseG2p() => new ArpabetG2p();
        protected override string[] GetBaseG2pVowels() => new string[] {
            "aa", "ae", "ah", "ao", "aw", "ay", "eh", "er", 
            "ey", "ih", "iy", "ow", "oy", "uh", "uw"
        };

        protected override string[] GetBaseG2pConsonants() => new string[] {
            "b", "ch", "d", "dh", "f", "g", "hh", "jh", "k", "l", "m", "n", 
            "ng", "p", "r", "s", "sh", "t", "th", "v", "w", "y", "z", "zh"
        };
    }
}