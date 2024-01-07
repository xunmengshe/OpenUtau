using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OpenUtau.Api;
using Serilog;
using WanaKanaNet;

namespace OpenUtau.Core.DiffSinger {
    [Phonemizer("DiffSinger Japanese Phonemizer", "DIFFS JA", language: "JA")]
    public class DiffSingerJapanesePhonemizer : DiffSingerBasePhonemizer {
        protected override IG2p LoadG2p(string rootPath) {
            var g2ps = new List<IG2p>();
            // Load dictionary from singer folder.
            string file = Path.Combine(rootPath, "dsdict.yaml");
            string file_jp = Path.Combine(rootPath, "dsdict-jp.yaml");
            if (File.Exists(file_jp)) {
                try {
                    g2ps.Add(G2pDictionary.NewBuilder().Load(File.ReadAllText(file_jp)).Build());
                } catch (Exception e) {
                    Log.Error(e, $"Failed to load {file_jp}");
                }
            } else if (File.Exists(file)) {
                try {
                    g2ps.Add(G2pDictionary.NewBuilder().Load(File.ReadAllText(file)).Build());
                } catch (Exception e) {
                    Log.Error(e, $"Failed to load {file}");
                }
            }
            return new G2pFallbacks(g2ps.ToArray());
        }

        protected override string[] Romanize(IEnumerable<string> lyrics) {
            var lyricsArray = lyrics.ToArray();
            var hiraganaLyrics = String.Join(" ", lyricsArray
                .Where(IsHiragana));
            var pinyinResult = WanaKana.ToRomaji(hiraganaLyrics).ToLower().Split();
            var pinyinIndex = 0;
            for (int i = 0; i < lyricsArray.Length; i++) {
                if (IsHiragana(lyricsArray[i])) {
                    lyricsArray[i] = pinyinResult[pinyinIndex];
                    pinyinIndex++;
                }
            }
            return lyricsArray;
        }

        public static bool IsHiragana(string lyric) {
            return lyric.Length <= 2 && Regex.IsMatch(lyric, "[ぁ-んァ-ヴ]");
        }
    }
}
