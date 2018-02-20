using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static Utility.Hashing;

namespace BruteGen {
    class Program {
        static string resumeStateFileName = "resume_state.json";

        class GenConfig {
            public int batch_size = 10000000;//tex number of strings to generate before writing to output (both disk and console), resume state will also be saved. The time it will take to complete a batch also depends on how many words and the length of the word lists. There's also a trade off between how often it saves progress vs the i/o overhead of doing so. 
            public string words_base_path = "";//tex used to set working path to allow words_paths be relative thus shorter.
            public List<string> words_paths = new List<string>();//tex path to file of strings per word, file must have .txt extension, but provided path can be without, the loading method will deal with it.
            public HashSet<string> word_variations_all = new HashSet<string>();//tex will add a variation for each word in a wordlist
            public string output_path = null; //tex optional, will fall back to wordspath, path to write strings and resume_state to.
            public string test_hashes_path = null;//tex optional, folder or file path of hashes to test against
            public string test_hashes_func = null;//tex optional if not using test_hashes_path, name of hash function to use (see the HashWrangler tool).
        }

        class HashInfo {
            public HashFunction HashFunc = StrCode32Str;
            public HashSet<string> inputHashes = null;
        }

        struct RunState {
            public int currentDepth;//tex progress of generation across words
            public int currentWordListIndex;//tex index down into list for a word
            public string currentString;//tex string being built, product of previous iterations
        }

        static void Main(string[] args) {
            if (args.Length == 0) {
                //ShowUsageInfo();//TODO:
                return;
            }

            string configPath = GetPath(args[0]);
            if (configPath == null) {
                Console.WriteLine("ERROR: Could not find .json file for path " + args[0]);
                return;
            }
            if (!configPath.Contains(".json")) {
                Console.WriteLine("ERROR: path does not contain '.json' :" + configPath);
                return;
            }


            string jsonString = File.ReadAllText(configPath);
            GenConfig config = JsonConvert.DeserializeObject<GenConfig>(jsonString);
            //TODO: exception handling

            //TODO: VALIDATE config
            //verify all paths
            //verify critical values are set
            config.words_base_path = GetPath(config.words_base_path);
            config.test_hashes_path = GetPath(config.test_hashes_path);
            config.output_path = GetPath(config.output_path);
            if (config.output_path == null) {
                config.output_path = config.words_base_path;
            }

            if (!Directory.Exists(config.output_path)) {
                Console.WriteLine("ERROR: could not find config.outputPath: " + config.output_path);
                return;
            }

            if (!Directory.Exists(config.words_base_path)) {
                Console.WriteLine("ERROR: could not find config.words_path: " + config.words_base_path);
                return;
            }

            Directory.SetCurrentDirectory(config.words_base_path);

            if (config.test_hashes_func != null) {
                config.test_hashes_func = config.test_hashes_func.ToLower();
            }

            string outputName = Path.GetFileNameWithoutExtension(configPath);

            //read resume state
            string resumeStatePath = Path.Combine(config.output_path, outputName + "-" + resumeStateFileName);


            // hash test info
            var hashInfo = new HashInfo();
            if (config.test_hashes_path != null) {
                Console.WriteLine("Reading input hashes:");
                hashInfo.inputHashes = GetStrings(config.test_hashes_path);
                if (hashInfo.inputHashes == null) {
                    Console.WriteLine("ERROR: no hashes in inputhashes");
                    return;
                }

                try {
                    hashInfo.HashFunc = hashFuncs[config.test_hashes_func.ToLower()];
                } catch (KeyNotFoundException) {
                    hashInfo.HashFunc = StrCode32Str;
                    Console.WriteLine("ERROR: Could not find hash function " + config.test_hashes_func);
                    return;
                }
            }

            if (hashInfo.inputHashes != null) {
                Console.WriteLine("Will test strings with " + config.test_hashes_func);
            }

            Console.WriteLine("Reading words lists");
            var allWordsLists = new List<string>[config.words_paths.Count];
            for (int i = 0; i < allWordsLists.Length; i++) {
                string wordPath = config.words_paths[i];
                if (!wordPath.Contains(".txt")) {
                    wordPath = wordPath += ".txt";
                }
                List<string> wordsList = GetStrings(wordPath).ToList<string>();
                if (wordsList == null) {
                    Console.WriteLine("ERROR file or dir " + wordPath + " not found");
                    return;
                }

                if (wordsList.Count == 0) {
                    Console.WriteLine("WARNING: wordslist for " + wordPath + " is empty");
                }

                allWordsLists[i] = wordsList;
            }

            GenerateWordVariations(config, ref allWordsLists);

            string wordCounts = "Word counts: ";
            for (int i = 0; i < allWordsLists.Length; i++) {
                wordCounts += " " + allWordsLists[i].Count;
            }
            Console.WriteLine(wordCounts);


            int batchSize = config.batch_size;
            Console.WriteLine("Batch size:" + batchSize);

            Stack<RunState> resumeState = null;
            if (File.Exists(resumeStatePath)) {
                Console.WriteLine("Reading resume_state");
                string resumeJson = File.ReadAllText(resumeStatePath);
                resumeState = JsonConvert.DeserializeObject<Stack<RunState>>(resumeJson);
                //TODO exception handling
            }

            string stringsOutPath = Path.Combine(config.output_path, outputName + ".txt");

            GenerateStrings(resumeState, allWordsLists, hashInfo, batchSize, resumeStatePath, stringsOutPath);

            if (File.Exists(resumeStatePath)) {
                File.Delete(resumeStatePath);
            }

            //SimpleTests(allWordsLists);

            Console.WriteLine("done");
        }

        static void GenerateStrings(Stack<RunState> resumeState, List<string>[] allWordsLists, HashInfo hashInfo, int batchSize, string resumeStatePath, string stringsOutPath) {
            bool isResume = resumeState != null;


            int batchCount = 0;

            List<int> recurseState = new List<int>(new int[allWordsLists.Length]);//tex purely for user to get an idea of how it's progressing

            Stack<RunState> stack = new Stack<RunState>();
            if (resumeState == null) {
                RunState startState = new RunState();
                startState.currentDepth = 0;
                startState.currentWordListIndex = 0;
                startState.currentString = "";

                stack.Push(startState);
            } else {
                // tex more stack order shenanigans, dont ask me why
                while (resumeState.Count > 0) {
                    stack.Push(resumeState.Pop());
                }
            }

            if (!isResume) {
                Console.WriteLine("Starting GenerateStrings");
            } else {
                Console.WriteLine("Resuming GenerateStrings");
            }

            using (StreamWriter sw = new StreamWriter(stringsOutPath, isResume))
            {
                while (stack.Count > 0) {
                    RunState state = stack.Pop();

                    //tex you can output currentString here if you want to catch each stage of generation instead of just complete string below
                    //but I currently prefer just having empty strings in the word lists for per word control
                    if (state.currentDepth == allWordsLists.Length)//tex completed whole string
                    {
                        if (hashInfo.inputHashes == null) {
                            sw.WriteLine(state.currentString);
                        } else {
                            var hash = hashInfo.HashFunc(state.currentString);
                            if (hashInfo.inputHashes.Contains(hash)) {
                                sw.WriteLine(state.currentString);
                            }
                        }
                        batchCount++;

                        //tex write/flush current strings and write resume_state
                        if (batchCount >= batchSize) {
                            batchCount = 0;

                            sw.Flush();

                            string jsonStringOut = JsonConvert.SerializeObject(stack);
                            File.WriteAllText(resumeStatePath, jsonStringOut);

                            string rs = "";
                            foreach (int index in recurseState) {
                                rs += " " + index;
                            }
                            Console.WriteLine(rs + "          : " + state.currentString);
                        }
                    } else {
                        recurseState[state.currentDepth] = state.currentWordListIndex;

                        List<string> wordList = allWordsLists[state.currentDepth];
                        //tex due to stack the order is actually reversed compared to recursion
                        // for (int wordListIndex = 0; wordListIndex < wordList.Count; wordListIndex++)
                        for (int wordListIndex = wordList.Count - 1; wordListIndex >= 0; wordListIndex--) {
                            RunState nextState = new RunState();
                            nextState.currentDepth = state.currentDepth + 1;
                            nextState.currentWordListIndex = wordListIndex;
                            nextState.currentString = state.currentString + wordList[wordListIndex];

                            stack.Push(nextState);
                        }
                    }
                }//while stack
            }//using sw
        }

        //REF tex stripped down so I can get a handle on it
        static void GenerateStringSimple_r(List<string>[] allWordsList, int currentDepth, int currentWordListIndex, string currentString) {
            if (currentDepth == allWordsList.Length) {
                Console.WriteLine(currentString);
                return;
            } else {
                List<string> wordList = allWordsList[currentDepth];
                for (int wordListIndex = 0; wordListIndex < wordList.Count; wordListIndex++) {
                    string word = wordList[wordListIndex];
                    string partialString = currentString + word;

                    GenerateStringSimple_r(allWordsList, currentDepth + 1, wordListIndex, partialString);
                }
            }
        }

        //REF tex state into struct
        static void GenerateStringSimple2_r(List<string>[] allWordsList, RunState state) {
            if (state.currentDepth == allWordsList.Length) {
                Console.WriteLine(state.currentString);
                return;
            } else {
                List<string> wordList = allWordsList[state.currentDepth];
                for (int wordListIndex = 0; wordListIndex < wordList.Count; wordListIndex++) {
                    RunState nextState = new RunState();
                    nextState.currentDepth = state.currentDepth + 1;
                    nextState.currentWordListIndex = wordListIndex;
                    nextState.currentString = state.currentString + wordList[wordListIndex];

                    GenerateStringSimple2_r(allWordsList, nextState);
                }
            }
        }

        //REF tex converted to from recursion to using a stack (which just uses program stack anyway).
        static void GenerateStringSimple_Stack(List<string>[] allWordsList) {
            Stack<RunState> stack = new Stack<RunState>();

            RunState startState = new RunState();
            startState.currentDepth = 0;
            startState.currentWordListIndex = 0;
            startState.currentString = "";

            stack.Push(startState);

            while (stack.Count > 0) {
                RunState runState = stack.Pop();

                if (runState.currentDepth == allWordsList.Length) {
                    Console.WriteLine(runState.currentString);
                } else {
                    List<string> wordList = allWordsList[runState.currentDepth];
                    for (int wordListIndex = 0; wordListIndex < wordList.Count; wordListIndex++) {
                        RunState nextState = new RunState();
                        nextState.currentDepth = runState.currentDepth + 1;
                        nextState.currentWordListIndex = wordListIndex;
                        nextState.currentString = runState.currentString + wordList[wordListIndex];

                        stack.Push(nextState);
                    }
                }
            }
        }

        //REF tex supersceded by recursionless
        static void GenerateString_r(HashInfo hashInfo, List<string>[] allWordsList, int currentDepth, int currentWordListIndex, string currentString, List<int> recurseState, int batchSize, ref int batchCount, string resumeStatePath, StreamWriter sw) {
            //tex completed whole string
            if (currentDepth == allWordsList.Length) {
                if (hashInfo.inputHashes != null) {
                    var hash = hashInfo.HashFunc(currentString);
                    if (hashInfo.inputHashes.Contains(hash)) {
                        sw.WriteLine(currentString);
                    }
                } else {
                    sw.WriteLine(currentString);
                }
                batchCount++;

                //tex write/flush current strings and write resume_state
                if (batchCount >= batchSize) {
                    batchCount = 0;

                    sw.Flush();

                    string jsonStringOut = JsonConvert.SerializeObject(recurseState);
                    File.WriteAllText(resumeStatePath, jsonStringOut);

                    string rs = "";
                    foreach (int index in recurseState) {
                        rs += " " + index;
                    }
                    Console.WriteLine(rs + "          : " + currentString);
                }

                return;
            } else {
                recurseState[currentDepth] = currentWordListIndex;

                List<string> wordList = allWordsList[currentDepth];
                for (int wordListIndex = 0; wordListIndex < wordList.Count; wordListIndex++) {
                    string word = wordList[wordListIndex];
                    string partialString = currentString + word;

                    GenerateString_r(hashInfo, allWordsList, currentDepth + 1, wordListIndex, partialString, recurseState, batchSize, ref batchCount, resumeStatePath, sw);
                }
            }
        }

        private static void SimpleTests(List<string>[] allWordsLists) {
            Console.WriteLine("!!!!SimpleTests");

            //tex filling out all variables to get concanical serialization.
            GenConfig config = new GenConfig();
            config.batch_size = 1;
            config.words_base_path = @"D:\GitHub\BruteGen\Examples\3x3 - test hashes";
            config.words_paths.Add("wordA");
            config.words_paths.Add("wordB");
            config.words_paths.Add("wordC");
            config.output_path = @"D:\GitHub\BruteGen\Examples\Examples Output";
            config.word_variations_all.Add("dont_add_original");
            config.word_variations_all.Add("all_upper");
            config.word_variations_all.Add("all_lower");
            config.word_variations_all.Add("capitalized");
            config.word_variations_all.Add("blank_optional");
            config.test_hashes_path = @"D:\GitHub\BruteGen\Examples\3x3 - test hashes\StrCode32Hashes";
            config.test_hashes_func = "StrCode32";

            JsonSerializerSettings serializeSettings = new JsonSerializerSettings();
            serializeSettings.Formatting = Formatting.Indented;
            string jsonStringOut = JsonConvert.SerializeObject(config, serializeSettings);
            string jsonOutPath = @"D:\GitHub\BruteGen\full-config.json";
            File.WriteAllText(jsonOutPath, jsonStringOut);


            RunState startStateS = new RunState();
            startStateS.currentDepth = 0;
            startStateS.currentWordListIndex = 0;
            startStateS.currentString = "";
            GenerateStringSimple2_r(allWordsLists, startStateS);

            GenerateStringSimple_Stack(allWordsLists);
        }

        private static void GenerateWordVariations(GenConfig genConfig, ref List<string>[] allWordsLists) {
            if (genConfig.word_variations_all.Count > 0) {
                Console.WriteLine("Generating variations for words lists");
                for (int i = 0; i < allWordsLists.Length; i++) {
                    //foreach (var wordsList in allWordsLists)

                    var wordsList = allWordsLists[i];
                    var expandedList = new HashSet<string>();


                    foreach (string word in wordsList) {
                        if (!genConfig.word_variations_all.Contains("dont_add_original")) {
                            expandedList.Add(word);
                        }
                        if (genConfig.word_variations_all.Contains("all_upper")) {
                            expandedList.Add(word.ToUpper());
                        }
                        if (genConfig.word_variations_all.Contains("all_lower")) {
                            expandedList.Add(word.ToLower());
                        }
                        if (genConfig.word_variations_all.Contains("capitalized")) {
                            if (word.Length > 1) {
                                expandedList.Add($"{word[0].ToString().ToUpper()}{word.Substring(1)}");
                            }
                        }
                    }

                    //tex after above since dont_add_original will clear non alphbetical strings since they have no case
                    if (genConfig.word_variations_all.Contains("blank_optional")) {
                        expandedList.Add("");
                    }

                    allWordsLists[i] = expandedList.ToList<string>();
                    allWordsLists[i].Sort();
                }
            }
        }

        private static string GetPath(string path) {
            if (Directory.Exists(path) || File.Exists(path)) {
                if (!Path.IsPathRooted(path)) {
                    path = Path.GetFullPath(path);
                }
            } else {
                path = null;
            }

            return path;
        }

        private static HashSet<string> GetStrings(string path) {
            List<string> files = GetFileList(path);
            if (files == null) {
                return null;
            }

            var strings = new HashSet<string>();
            foreach (string filePath in files) {
                foreach (string line in File.ReadLines(filePath)) {
                    strings.Add(line);
                }
            }
            return strings;
        }

        private static List<string> GetFileList(string inputPath) {
            if (!File.Exists(inputPath) && !Directory.Exists(inputPath)) {
                Console.WriteLine("WARNING: Could not find file or folder " + inputPath);
                return null;
            }

            if (!Path.IsPathRooted(inputPath)) {
                inputPath = Path.GetFullPath(inputPath);
            }

            List<string> fileList = new List<string>();
            if (File.Exists(inputPath)) {
                fileList.Add(inputPath);
            }
            if (Directory.Exists(inputPath)) {
                fileList = Directory.GetFiles(inputPath, "*.txt", SearchOption.AllDirectories).ToList<string>();
            }

            return fileList;
        }

        private static string GetJsonString(string path) {
            if (!File.Exists(path)) {
                return null;
            }

            if (!Path.IsPathRooted(path)) {
                path = Path.GetFullPath(path);
            }

            string jsonString = File.ReadAllText(path);
            return jsonString;
        }
    }
}
