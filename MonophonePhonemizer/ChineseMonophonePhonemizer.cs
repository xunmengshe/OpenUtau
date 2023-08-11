using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenUtau.Api;
using OpenUtau.Core;

namespace MonophonePhonemizer
{
    [Phonemizer("Chinese Monophone Phonemizer", "ZH MONO", language: "ZH")]
    public class ChineseMonophonePhonemizer: MonophoneG2pPhonemizer
    {
        protected override string GetDictionaryName()=>"mono-zh.yaml";
        public override void SetUp(Note[][] groups) {
            BaseChinesePhonemizer.RomanizeNotes(groups);
        }
    }
}