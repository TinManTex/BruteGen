using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Utility.Hashing;

namespace BruteGen {
    class Program {
        class GenConfig {
            public int batch_size = 10000000;//tex number of strings to generate before writing to output (both disk and console), resume state will also be saved. The time it will take to complete a batch also depends on how many words and the length of the word lists. There's also a trade off between how often it saves progress vs the i/o overhead of doing so. 
            public bool test_on_batch = true; //tex: optional, default true, will parallel test whole batch instead of on each whole string. This is much faster, but also uses more cpu. Setting to false can be useful if you want to trade off cpu usage for time (like if you're using your computer for other stuff but what brutegen to tick away in background).
            public string words_base_path = null;//tex optional, will fall back to path of config.json / arg[0], used to set working path to allow words_paths be relative thus shorter.
            public List<string> words_paths = new List<string>();//tex path to file of strings per word, file must have .txt extension, but provided path can be without, the loading method will deal with it.
            public HashSet<string> word_variations_all = new HashSet<string>();//tex will add a variation for each word in a wordlist
            public string output_path = null; //tex optional, will fall back to wordspath, path to write strings and resume_state to.
            public string test_hashes_path = null;//tex optional, folder or file path of hashes to test against. If set brutegen will only output generatted strings that match a hash, otherwise it will just output every string that was generated.
            public string test_hashes_func = null;//tex optional if not using test_hashes_path, name of hash function to use (see the HashWrangler tool).
        }

        //Just a helper that bundles a set of hashes with the function of their hash type.
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
                Console.WriteLine("Usage: BruteGen.exe <config .json path>");

                GenConfig defaultConfig = new GenConfig();
                JsonSerializerSettings serializeSettings = new JsonSerializerSettings();
                serializeSettings.Formatting = Formatting.Indented;
                string jsonStringOut = JsonConvert.SerializeObject(defaultConfig, serializeSettings);
                string jsonOutPath = Directory.GetCurrentDirectory() + @"\default-config.json";
                jsonOutPath = Regex.Replace(jsonOutPath, @"\\", "/");
                File.WriteAllText(jsonOutPath, jsonStringOut);

                Console.WriteLine($"Writing default config to {jsonOutPath}");
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

            GenConfig config = LoadConfig(configPath);

            if (!Directory.Exists(config.output_path)) {
                Console.WriteLine("ERROR: could not find config.outputPath: " + config.output_path);
                return;
            }

            if (!Directory.Exists(config.words_base_path)) {
                Console.WriteLine("ERROR: could not find config.words_path: " + config.words_base_path);
                return;
            }

            Directory.SetCurrentDirectory(config.words_base_path);


            string outputName = Path.GetFileNameWithoutExtension(configPath);//tex filename for resume stat and matches file.

            string resumeStatePath = Path.Combine(config.output_path, $"{outputName}-resume_state.json");

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
            var allWordsLists = GetWordsLists(config.words_paths);

            GenerateWordVariations(config, ref allWordsLists);

            string wordCounts = "Word counts: ";
            for (int i = 0; i < allWordsLists.Length; i++) {
                wordCounts += " " + allWordsLists[i].Count;
            }
            Console.WriteLine(wordCounts);

            Console.WriteLine("Batch size:" + config.batch_size);
            Stack<RunState> resumeState = ReadResumeState(resumeStatePath);

            string stringsOutPath = Path.Combine(config.output_path, outputName + ".txt");

            GenerateStrings(resumeState, allWordsLists, hashInfo, config.batch_size, config.test_on_batch, resumeStatePath, stringsOutPath);//tex the main generate/test loop

            if (File.Exists(resumeStatePath)) {
                File.Delete(resumeStatePath);
            }

            //SimpleTests(allWordsLists);

            Console.WriteLine("done");
        }//Main

        /// <summary>
        /// Load .json config
        /// </summary>
        /// <param name="configPath"></param>
        /// <returns></returns>
        private static GenConfig LoadConfig(string configPath) {
            string jsonString = File.ReadAllText(configPath);
            GenConfig config = JsonConvert.DeserializeObject<GenConfig>(jsonString);
            //TODO: exception handling

            //TODO: VALIDATE config
            //verify all paths
            //verify critical values are set
            config.words_base_path = GetPath(config.words_base_path);
            config.test_hashes_path = GetPath(config.test_hashes_path);
            config.output_path = GetPath(config.output_path);
            if (config.words_base_path == null) {
                config.words_base_path = Path.GetDirectoryName(configPath);
            }
            if (config.output_path == null) {
                config.output_path = config.words_base_path;
            }

            if (config.test_hashes_func != null) {
                config.test_hashes_func = config.test_hashes_func.ToLower();
            }

            return config;
        }//LoadConfig

        /// <summary>
        /// Read .json resume state
        /// </summary>
        /// <param name="resumeStatePath"></param>
        /// <returns></returns>
        private static Stack<RunState> ReadResumeState(string resumeStatePath) {
            Stack<RunState> resumeState = null;
            if (File.Exists(resumeStatePath)) {
                Console.WriteLine("Reading resume_state");
                string resumeJson = File.ReadAllText(resumeStatePath);
                resumeState = JsonConvert.DeserializeObject<Stack<RunState>>(resumeJson);
                //TODO exception handling
            }

            return resumeState;
        }//ReadResumeState

        /// <summary>
        /// Main work loop function
        /// </summary>
        /// <param name="resumeState"></param>
        /// <param name="allWordsLists"></param>
        /// <param name="hashInfo"></param>
        /// <param name="batchSize"></param>
        /// <param name="testOnBatch"></param>
        /// <param name="resumeStatePath"></param>
        /// <param name="stringsOutPath"></param>
        static void GenerateStrings(Stack<RunState> resumeState, List<string>[] allWordsLists, HashInfo hashInfo, int batchSize, bool testOnBatch, string resumeStatePath, string stringsOutPath) {
            var totalStopwatch = new System.Diagnostics.Stopwatch();

            totalStopwatch.Start();

            bool isResume = resumeState != null;

            int batchCount = 0;
            List<string> batch = new List<string>();

            List<int> recurseState = new List<int>(new int[allWordsLists.Length]);//tex purely for user to get an idea of how it's progressing

            //tex using a custom stack instead of the usual nested loops for generating strings, this allows the whole state to be saved of so program can be quit and resumed later.
            //tex initialize stack
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
            }//init stack

            if (!isResume) {
                Console.WriteLine("Starting GenerateStrings");
            } else {
                Console.WriteLine("Resuming GenerateStrings");
            }

            using (StreamWriter sw = new StreamWriter(stringsOutPath, isResume)) {
                while (stack.Count > 0) {
                    RunState state = stack.Pop();

                    //tex you can output currentString here if you want to catch each stage of generation instead of just complete string below
                    //but I currently prefer just having empty strings in the word lists for per word control

                    if (state.currentDepth == allWordsLists.Length)//tex generated whole test-string
                    {
                        if (!testOnBatch) {
                            //tex if no inputhashes then we just write every generated string
                            if (hashInfo.inputHashes == null) {
                                sw.WriteLine(state.currentString);
                            } else {

                                var hash = hashInfo.HashFunc(state.currentString);
                                if (hashInfo.inputHashes.Contains(hash)) {
                                    sw.WriteLine(state.currentString);
                                }
                            }
                        }//testOnBatch
                        batchCount++;
                        batch.Add(state.currentString);

                        //tex write/flush current strings and write resume_state
                        if (batchCount >= batchSize) {
                            batchCount = 0;

                            //tex write resume state
                            string jsonStringOut = JsonConvert.SerializeObject(stack);
                            File.WriteAllText(resumeStatePath, jsonStringOut);

                            //tex give user feedback
                            string rs = "";
                            foreach (int index in recurseState) {
                                rs += " " + index;
                            }
                            Console.WriteLine(rs + "          : " + state.currentString);//TODO: order is shifted by one in comparison to wordcounts output earlier in the program, figure out what's up.

                            //tex test batch
                            if (testOnBatch) {
                                BatchTest(hashInfo, batch, sw);
                            }//testOnBatch

                            //tex clear batch and flush/write matches streamwriter
                            batch = new List<string>();
                            sw.Flush();
                        }//batchSize
                    } else {//tex recurse
                        recurseState[state.currentDepth] = state.currentWordListIndex;

                        List<string> wordList = allWordsLists[state.currentDepth];
                        //tex due to stack the order is actually reversed compared to recursion
                        // for (int wordListIndex = 0; wordListIndex < wordList.Count; wordListIndex++)
                        for (int wordListIndex = wordList.Count - 1; wordListIndex >= 0; wordListIndex--) {
                            //tex this is where we'd normally recursively call the generation function with the current state of the partially generated string
                            //instead we're just seting up our stack of that state, for the next loop
                            RunState nextState = new RunState();
                            nextState.currentDepth = state.currentDepth + 1;
                            nextState.currentWordListIndex = wordListIndex;
                            nextState.currentString = state.currentString + wordList[wordListIndex];

                            stack.Push(nextState);
                        }
                    }//if generated whole word
                }//while stack

                //tex need to process uncomplete batch
                if (batch.Count > 0) {
                    //tex test batch
                    if (testOnBatch) {
                        BatchTest(hashInfo, batch, sw);
                    }//testOnBatch

                    //tex clear batch and flush/write matches streamwriter
                    batch = new List<string>();

                    sw.Flush();

                }//batch.Count > 0
            }//using sw

            totalStopwatch.Stop();
            var timeSpan = totalStopwatch.Elapsed;
            Console.WriteLine($"GenerateStrings completed in {timeSpan.Hours}:{timeSpan.Minutes}:{timeSpan.Seconds}:{timeSpan.Milliseconds}");

        }//GenerateStrings

        /// <summary>
        /// Parallel test a batch of generated strings
        /// </summary>
        /// <param name="hashInfo"></param>
        /// <param name="batch"></param>
        /// <param name="sw"></param>
        private static void BatchTest(HashInfo hashInfo, List<string> batch, StreamWriter sw) {
            ConcurrentBag<string> matches = new ConcurrentBag<string>();
            Parallel.ForEach(batch, currentString => {
                if (hashInfo.inputHashes == null) {
                    matches.Add(currentString);
                } else {

                    var hash = hashInfo.HashFunc(currentString);
                    if (hashInfo.inputHashes.Contains(hash)) {
                        matches.Add(currentString);
                    }
                }
            });//foreach batch

            foreach (string match in matches) {
                sw.WriteLine(match);
            }
        }//BatchTest

        //REF tex stripped down GenerateString_r so I can get a handle on it
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
        }//GenerateStringSimple_r

        //REF tex evolution of above with state moved into struct
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
        }//GenerateStringSimple2_r

        //REF tex converted from recursion (above) to using a stack object (recursion just uses program stack anyway).
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
        }//GenerateStringSimple_Stack

        //REF tex original recursion version, supersceded by GenerateStrings
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
        }//GenerateString_r

        /// <summary>
        /// Build an array of string lists for a given list of string files.
        /// </summary>
        /// <param name="wordPaths"></param>
        /// <returns></returns>
        private static List<string>[] GetWordsLists(List<string> wordPaths) {
            List<string>[] allWordsLists = new List<string>[wordPaths.Count];
            for (int i = 0; i < allWordsLists.Length; i++) {
                string wordPath = wordPaths[i];
                if (!wordPath.Contains(".txt")) {
                    wordPath = wordPath += ".txt";
                }
                List<string> wordsList = GetStrings(wordPath).ToList<string>();
                if (wordsList == null) {
                    Console.WriteLine("ERROR file or dir " + wordPath + " not found");
                    return null;
                }

                if (wordsList.Count == 0) {
                    Console.WriteLine("WARNING: wordslist for " + wordPath + " is empty");
                }

                allWordsLists[i] = wordsList;
            }
            return allWordsLists;
        }//GetWordsLists

        /// <summary>
        /// Generates various variations of strings for a given list.
        /// </summary>
        /// <param name="genConfig"></param>
        /// <param name="allWordsLists"></param>
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
        }//GenerateWordVariations

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
        }//SimpleTests
    }//Program
}
