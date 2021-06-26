using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hashing;
using static Hashing.HashFuncs;

/// <summary>
// BruteGen
// Tool for generating strings for dictionary attacks, optional testing vs Fox Engine hashes.
// Will read lists of strings('words') and combine them in order to generate each string.
//
// Should be easy enough to adapt for testing other types of hash by editing the HashFuncs class.
// There's some performance loss since hash comparisons are done in string, but it simplifies things.
/// </summary>
namespace BruteGen {
    class Program {
        //tex .json config
        class GenConfig {
            public int batch_size = 10000000;//tex number of strings to generate before writing to output (both disk and console), resume state will also be saved. The time it will take to complete a batch also depends on how many words and the length of the word lists. There's also a trade off between how often it saves progress vs the i/o overhead of doing so. 
            public bool test_on_batch = true; //tex: optional, default true, will parallel test whole batch instead of on each whole string. This is much faster, but also uses more cpu. Setting to false can be useful if you want to trade off cpu usage for time (like if you're using your computer for other stuff but what brutegen to tick away in background). Another side effect (due to the parallelization) is the order of written strings may be different.
            public bool lockstep_same_word_lists = false;//tex word lists with the same filename will advance in lockstep, this allows you to build strings with repeating words in unison.
            public string words_base_path = null;//tex optional, will fall back to path of config.json / arg[0], used to set working path to allow words_paths be relative thus shorter.
            public List<string> words_paths = new List<string>();//tex path to file of strings per word, file must have .txt extension, but provided path can be without, the loading method will deal with it.
            public HashSet<string> word_variations_all = new HashSet<string>();//tex will add a variation for each word in a wordlist
            public string output_path = null; //tex optional, will fall back to wordspath, path to write strings and resume_state to.
            public string test_hashes_path = null;//tex optional, folder or file path of hashes to test against. If set brutegen will only output generatted strings that match a hash, otherwise it will just output every string that was generated.
            public string test_hashes_func = null;//tex optional if not using test_hashes_path, name of hash function to use (see the HashWrangler tool).
        }

        //tex Just a helper that bundles a set of hashes with the function of their hash type.
        class HashInfo {
            public HashFunction HashFunc = null;
            public HashSet<string> inputHashes = null;
        }

        //tex LEGACY CULL state of 1 level of generation recusion/stack
        struct RecurseStep {
            public int currentDepth;//tex progress of generation across words
            public int currentWordListIndex;//tex index down into list for a word
            public string currentString;//tex string being built, product of previous iterations
        }

        struct RunState {
            public int[] recurseState;//tex current wordIndexes for each wordsList
            public int[] completionState;//tex number of times list completed for each wordsList
        }

        /// <summary>
        /// Main, entry point
        /// </summary>
        /// <param name="args">args[0] == .json config path</param>
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


            string outputName = Path.GetFileNameWithoutExtension(configPath);//tex filename for resume state and matches file.

            string resumeStatePath = Path.Combine(config.output_path, $"{outputName}-resume_state.json");

            // hash test info
            var hashInfo = new HashInfo();
            if (config.test_hashes_path != null) {
                Console.WriteLine("Reading input hashes:");
                int maxLength;
                hashInfo.inputHashes = GetStrings(config.test_hashes_path, out maxLength);
                if (hashInfo.inputHashes == null) {
                    Console.WriteLine("ERROR: no hashes found in " + config.test_hashes_path);
                    return;
                }

                if (config.test_hashes_func == null) {
                    Console.WriteLine("ERROR: Could not find test_'hashes_func' in config");
                    return;
                }

                hashInfo.HashFunc = HashFuncs.GetHashFuncByName(config.test_hashes_func);
                if (hashInfo.HashFunc == null) {
                    Console.WriteLine("ERROR: Could not find hash function " + config.test_hashes_func);
                    return;
                }

                if (hashInfo.inputHashes != null) {
                    Console.WriteLine("Will test strings with " + config.test_hashes_func);
                }
            }//if test_hashes_path

            Console.WriteLine("Reading words lists");
            int[] maxWordLengths;
            var allWordsLists = GetWordsLists(config.words_paths, out maxWordLengths);
            int maxStringLength = maxWordLengths.Sum();

            GenerateWordVariations(config, ref allWordsLists);

            Console.WriteLine("Word counts:");
            string listNames = " ";
            for (int i = 0; i < allWordsLists.Length; i++) {
                listNames += Path.GetFileName(config.words_paths[i]) + "\t";
            }
            Console.WriteLine(listNames);

            string wordCounts = " ";
            for (int i = 0; i < allWordsLists.Length; i++) {
                wordCounts += allWordsLists[i].Count + "\t";
            }
            Console.WriteLine(wordCounts);

            Console.WriteLine("Batch size:" + config.batch_size);
            
            string stringsOutPath = Path.Combine(config.output_path, outputName + ".txt");

            //LEGACY CULL
            //Stack<RecurseStep> resumeStackState = ReadResumeStackState(resumeStatePath);
            //GenerateStringsStack(resumeStackState, allWordsLists, hashInfo, config.batch_size, config.test_on_batch, resumeStatePath, stringsOutPath);

            int[] lockstepIds = new int[allWordsLists.Length];
            int[] lockstepHeads = new int[allWordsLists.Length];//tex maximum number of ids is actually wordsLists.Length / 2 + 1 (id 0 for 'not a lockstep list').
            if (config.lockstep_same_word_lists) {
                BuildLockstepInfo(config.words_paths, ref lockstepIds, ref lockstepHeads);
            }//config.lockstep_same_word_lists


            GenerateStrings(allWordsLists, lockstepIds, lockstepHeads, hashInfo, maxStringLength, config.batch_size, config.test_on_batch, resumeStatePath, stringsOutPath);//tex the main generate/test loop

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
        /// <param name="resumeStatePath">.json file path</param>
        /// <returns>resume state</returns>
        private static RunState ReadResumeState(string resumeStatePath, int listSize) {
            RunState resumeState = new RunState();
            if (File.Exists(resumeStatePath)) {
                Console.WriteLine("Reading resume_state");
                string resumeJson = File.ReadAllText(resumeStatePath);
                resumeState = JsonConvert.DeserializeObject<RunState>(resumeJson);
                //TODO exception handling
            } else {
                resumeState.recurseState = new int[listSize];
                resumeState.completionState = new int[listSize];
            }
            return resumeState;
        }//ReadResumeState

        /// Read .json resume state //LEGACY CULL
        private static Stack<RecurseStep> ReadResumeStackState(string resumeStatePath) {
            Stack<RecurseStep> resumeState = null;
            if (File.Exists(resumeStatePath)) {
                Console.WriteLine("Reading resume_state");
                string resumeJson = File.ReadAllText(resumeStatePath);
                resumeState = JsonConvert.DeserializeObject<Stack<RecurseStep>>(resumeJson);
                //TODO exception handling
            }
            return resumeState;
        }//ReadResumeState

        /// <summary>
        /// Uses identical names of word lists to work out infor for advancing in lockstep.
        /// </summary>
        /// <param name="wordsPaths"></param>
        /// <param name="lockstepIds">ref ids indexed by wordsList 0 if not a lockstep list</param>
        /// <param name="lockstepHeads">ref highest/rightmost list for a lockstep, indexed by lockstepId</param>
        private static void BuildLockstepInfo(List<string> wordsPaths, ref int[] lockstepIds, ref int[] lockstepHeads) {
            int lockstepId = 0;
            for (int i = wordsPaths.Count - 1; i >= 0; i--) {
                string namei = Path.GetFileName(wordsPaths[i]);
                //tex find matching lists
                bool foundLockstep = false;
                for (int j = i - 1; j >= 0; j--) {
                    string namej = Path.GetFileName(wordsPaths[j]);
                    if (namej == namei) {
                        //tex new lockstep who dis?
                        if (!foundLockstep) {
                            foundLockstep = true;
                            lockstepId++;
                            lockstepIds[i] = lockstepId;
                            lockstepHeads[lockstepId] = i;
                        }
                        lockstepIds[j] = lockstepId;
                    }
                }//for lists descending
            }//for filenames
        }//BuildLockstepInfo

        /// <summary>
        /// Main work loop function
        /// A common string generation method is to use depth first recursion, with a depth cut-off, this is to allow partial completions to cover all combinations.
        /// Thus the current state is tied up in the stack.
        /// However if you shift empty strings to the words lists it becomes simpler to maintain a state of the current word indexes for each word list,
        /// build the string from each wordlist/wordIndex, then just advance the wordIndexes.
        /// This also makes it simpler to save off that state so a run can be quit and resumed.
        /// Also (with a bit more complicated advance function) allows for lists to advance in lockstep.
        /// </summary>
        /// IO-IN: Resume state .json
        /// IO-OUT: Resume state .json
        /// IO-OUT: Generated/matched strings .txt
        static void GenerateStrings(List<string>[] wordsLists, int[] lockstepIds, int[] lockstepHeads, HashInfo hashInfo, int maxStringLength, int batchSize, bool testOnBatch, string resumeStatePath, string stringsOutPath) {
            var totalStopwatch = new System.Diagnostics.Stopwatch();
            totalStopwatch.Start();
            int loopCount = 0;

            RunState runState = ReadResumeState(resumeStatePath, wordsLists.Length);//tex IO-IN
            bool isResume = File.Exists(resumeStatePath);

            List<string> batch = new List<string>();

            int[] listSizes = new int[wordsLists.Length];

            for (int i = 0; i < wordsLists.Count(); i++) {
                listSizes[i] = wordsLists[i].Count();
            }

            if (!isResume) {
                Console.WriteLine("Starting GenerateStrings");
            } else {
                Console.WriteLine("Resuming GenerateStrings");
            }

            StringBuilder stringBuilder = new StringBuilder(maxStringLength);

            using (StreamWriter outputStringsStream = new StreamWriter(stringsOutPath, isResume)) {//tex IO-OUT StreamWriter append = isResume
                do {
                    loopCount++;
                    
                    string currentString = "";
                    stringBuilder.Clear();
                    for (int listIndex = 0; listIndex < wordsLists.Length; listIndex++) {
                        int wordIndex = runState.recurseState[listIndex];
                        var wordList = wordsLists[listIndex];
                        string word = wordList[wordIndex];
                        stringBuilder.Append(word);//tex appending to a string is slower than stringbuilder at the rate/amount of strings we're using.
                    }
                    currentString = stringBuilder.ToString();

                    if (!testOnBatch) {
                        //tex if no inputhashes then we just write every generated string
                        if (hashInfo.inputHashes == null) {
                            outputStringsStream.WriteLine(currentString);
                        } else {
                            var hash = hashInfo.HashFunc(currentString);
                            if (hashInfo.inputHashes.Contains(hash)) {
                                outputStringsStream.WriteLine(currentString);
                            }
                        }
                    }//testOnBatch
                    batch.Add(currentString);

                    //tex write/flush current strings and write resume_state
                    if (batch.Count >= batchSize) {
                        //tex IO-OUT: write resume state
                        string jsonStringOut = JsonConvert.SerializeObject(runState);
                        File.WriteAllText(resumeStatePath, jsonStringOut);

                        //tex give user feedback
                        string rs = " ";
                        foreach (int wordIndex in runState.recurseState) {
                            rs += wordIndex + "\t";
                        }
                        Console.WriteLine(rs + "\t\t: " + currentString);

                        //tex test batch
                        if (testOnBatch) {
                            BatchTest(hashInfo, batch, outputStringsStream);
                        }//testOnBatch

                        //tex clear batch and flush/write matches
                        batch = new List<string>();
                        outputStringsStream.Flush();
                    }//batchSize
                } while (AdvanceState_r(wordsLists.Length - 1, listSizes, lockstepIds, lockstepHeads, ref runState));

                //tex need to process incomplete batch
                if (batch.Count > 0) {
                    //tex test batch
                    if (testOnBatch) {
                        BatchTest(hashInfo, batch, outputStringsStream);
                    }//testOnBatch

                    //tex clear batch and flush/write matches streamwriter
                    batch = new List<string>();
                    outputStringsStream.Flush();
                }//batch.Count > 0
            }//using sw

            //tex print stats
            totalStopwatch.Stop();
            var timeSpan = totalStopwatch.Elapsed;
            Console.WriteLine($"GenerateStrings completed in {timeSpan.Hours}:{timeSpan.Minutes}:{timeSpan.Seconds}:{timeSpan.Milliseconds}");
            Console.WriteLine($"LoopCount: {loopCount}");//tex should match the product of wordsLists counts.
            Console.WriteLine("Completion count:");
            string cs = " ";
            foreach (int completionCount in runState.completionState) {
                cs += completionCount + "\t";
            }
            Console.WriteLine(cs);
        }//GenerateStrings

        /// <summary>
        /// REF LEGACY version prior to lockstep implemenation
        /// Advances a wordlist, if the wordlist hits it's max it wraps to 0 then is called to advance the previous list.
        /// Function is initially called with listIndex of the last list.
        /// Think of it like an odometer
        /// </summary>
        /// <param name="recurseState">Current wordIndexes of all lists</param>
        /// <returns>false if first/lowest list has wrapped (thus all complete)</returns>
        private static bool AdvanceStateSimple_r(int listIndex, int[] listSizes, ref int[] recurseState) {
            recurseState[listIndex]++;//tex next word
            if (recurseState[listIndex] >= listSizes[listIndex]) {//tex hit end of list, wrap
                recurseState[listIndex] = 0;
                //tex advance previous list
                listIndex--;
                if (listIndex < 0) {//tex unless we've done the first list, then we're done
                    return false;
                } else {
                    return AdvanceStateSimple_r(listIndex, listSizes, ref recurseState);
                }
            }
            return true;
        }//AdvanceStateSimple_r

        /// <summary>
        /// Advances a wordlist, if the wordlist hits it's max it wraps to 0 then is called to advance the previous list.
        /// Function is initially called with listIndex of the last/highest list.
        /// Think of it like an odometer, except some of the counters can advance in lockstep.
        /// </summary>
        /// <param name="listIndex">Index of word list we want to try and advance.</param>
        /// <param name="listSizes">Indexed by wordList</param>
        /// <param name="lockstepIds">A config can have multiple word lists that advance in lockstep, indexed by wordList</param>
        /// <param name="lockstepHeads">Highest/rightmost list of word lists that advance in lockstep, indexed by lockstepId</param>
        /// <param name="runState">ref Current wordIndexes of all lists and completion state</param>
        /// <returns>continueAdvance - false if first/lowest list has wrapped (thus all complete)</returns>
        private static bool AdvanceState_r(int listIndex, int[] listSizes, int[] lockstepIds, int[] lockstepHeads, ref RunState runState) {
            int lockstepId = lockstepIds[listIndex];
            int lockstepHead = lockstepHeads[lockstepId];

            bool wrapped = false;
            if (lockstepId == 0) {//tex id 0 == always advance (not a lockstep list)
                wrapped = AdvanceList(listIndex, listSizes, runState);
            } else if (lockstepHead == listIndex) {//tex only the head of a lockstep can be advanced
                for (int i = lockstepHead; i >= 0; i--) {
                    if (lockstepIds[i] == lockstepId) {
                        wrapped = AdvanceList(i, listSizes, runState);
                    }
                }
            }//tex advance and wrap
       
            if (wrapped) {
                bool allComplete = CheckComplete(runState.completionState);
                if (allComplete) {
                    return false;
                }

                //tex find a lower list to advance
                int previousList = listIndex - 1;
                for (int i = previousList; i >= 0; i--) {
                    int prevLockstepId = lockstepIds[i];
                    int preLockstepHead = lockstepHeads[prevLockstepId];
                    //tex only non lockstep (id 0) or the head (rightmost) lockstep list can be advanced
                    if (prevLockstepId == 0 || preLockstepHead == i) {
                        previousList = i;
                        break;
                    }
                }//for lists reverse

                //tex unless we've done the first list
                if (previousList < 0) {
                    //tex doesn't really work if we have lockstepping in the mix.
                    //return false;//tex then we're done
                } else {
                    //tex advance lower list
                    return AdvanceState_r(previousList, listSizes, lockstepIds, lockstepHeads, ref runState);
                }
            }
            return true;
        }//

        /// <summary>
        /// Advance list and completion state on wrap
        /// </summary>
        /// <param name="listIndex"></param>
        /// <param name="listSizes"></param>
        /// <param name="runState"></param>
        /// <returns>true if wrapped</returns>
        private static bool AdvanceList(int listIndex, int[] listSizes, RunState runState) {
            runState.recurseState[listIndex]++;//tex next word
            if (runState.recurseState[listIndex] >= listSizes[listIndex]) {//tex hit end of list, wrap
                runState.recurseState[listIndex] = 0;
                runState.completionState[listIndex]++;
                return true;
            }
            return false;
        }

        private static bool CheckComplete(int[] completionState) {
            //tex check to see if we're done
            bool allComplete = true;
            for (int i = 0; i < completionState.Length; i++) {
                if (completionState[i] == 0) {
                    allComplete = false;
                    break;
                }
            }

            return allComplete;
        }

        /// <summary>
        /// Parallel test a batch of generated strings
        /// </summary>
        /// <param name="hashInfo"></param>
        /// <param name="batch"></param>
        /// <param name="outputStringsStream"></param>
        private static void BatchTest(HashInfo hashInfo, List<string> batch, StreamWriter outputStringsStream) {
            ConcurrentBag<string> matches = new ConcurrentBag<string>();
            Parallel.ForEach(batch, currentString => {
                if (hashInfo.inputHashes == null) {//tex if no inputhashes then we just write every generated string
                    matches.Add(currentString);
                } else {
                    var hash = hashInfo.HashFunc(currentString);
                    if (hashInfo.inputHashes.Contains(hash)) {
                        matches.Add(currentString);
                    }
                }
            });//foreach batch

            foreach (string match in matches) {
                outputStringsStream.WriteLine(match);
            }
        }//BatchTest

        //REF CULL Old stack based
        static void GenerateStringsStack(Stack<RecurseStep> resumeState, List<string>[] allWordsLists, HashInfo hashInfo, int batchSize, bool testOnBatch, string resumeStatePath, string stringsOutPath) {
            var totalStopwatch = new System.Diagnostics.Stopwatch();

            totalStopwatch.Start();

            int loopCount = 0;

            bool isResume = resumeState != null;

            List<string> batch = new List<string>();

            List<int> recurseState = new List<int>(new int[allWordsLists.Length]);//tex purely for user to get an idea of how it's progressing

            //tex using a custom stack instead of the usual nested loops for generating strings, this allows the whole state to be saved of so program can be quit and resumed later.
            //tex initialize stack
            Stack<RecurseStep> stack = new Stack<RecurseStep>();
            if (resumeState == null) {
                RecurseStep startState = new RecurseStep();
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

            using (StreamWriter sw = new StreamWriter(stringsOutPath, isResume)) {//tex StreamWriter append = isResume
                while (stack.Count > 0) {
                    loopCount++;
                    RecurseStep state = stack.Pop();

                    //tex you can output currentString here if you want to catch each stage of generation instead of just complete string below
                    //but I currently prefer just having empty strings in the word lists for per word control

                    if (state.currentDepth == allWordsLists.Length) {//tex generated whole test-string
                        string currentString = state.currentString;

                        if (!testOnBatch) {
                            //tex if no inputhashes then we just write every generated string
                            if (hashInfo.inputHashes == null) {
                                sw.WriteLine(currentString);
                            } else {
                                var hash = hashInfo.HashFunc(currentString);
                                if (hashInfo.inputHashes.Contains(hash)) {
                                    sw.WriteLine(currentString);
                                }
                            }
                        }//testOnBatch
                        batch.Add(currentString);

                        //tex write/flush current strings and write resume_state
                        if (batch.Count >= batchSize) {
                            //tex write resume state
                            string jsonStringOut = JsonConvert.SerializeObject(stack);
                            File.WriteAllText(resumeStatePath, jsonStringOut);

                            //tex give user feedback
                            string rs = "";
                            foreach (int wordIndex in recurseState) {
                                rs += " " + wordIndex;
                            }
                            Console.WriteLine(rs + "          : " + currentString);//TODO: order is shifted by one in comparison to wordcounts output earlier in the program, figure out what's up.

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
                            RecurseStep nextState = new RecurseStep();
                            nextState.currentDepth = state.currentDepth + 1;
                            nextState.currentWordListIndex = wordListIndex;
                            nextState.currentString = state.currentString + wordList[wordListIndex];

                            stack.Push(nextState);
                        }
                    }//if generated whole word
                }//while stack

                //tex need to process incomplete batch
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
            Console.WriteLine($"LoopCount: {loopCount}");

        }//GenerateStrings

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
        static void GenerateStringSimple2_r(List<string>[] allWordsList, RecurseStep state) {
            if (state.currentDepth == allWordsList.Length) {
                Console.WriteLine(state.currentString);
                return;
            } else {
                List<string> wordList = allWordsList[state.currentDepth];
                for (int wordListIndex = 0; wordListIndex < wordList.Count; wordListIndex++) {
                    RecurseStep nextState = new RecurseStep();
                    nextState.currentDepth = state.currentDepth + 1;
                    nextState.currentWordListIndex = wordListIndex;
                    nextState.currentString = state.currentString + wordList[wordListIndex];

                    GenerateStringSimple2_r(allWordsList, nextState);
                }
            }
        }//GenerateStringSimple2_r

        //REF tex converted from recursion (above) to using a stack object (recursion just uses program stack anyway).
        static void GenerateStringSimple_Stack(List<string>[] allWordsList) {
            Stack<RecurseStep> stack = new Stack<RecurseStep>();

            RecurseStep startState = new RecurseStep();
            startState.currentDepth = 0;
            startState.currentWordListIndex = 0;
            startState.currentString = "";

            stack.Push(startState);

            while (stack.Count > 0) {
                RecurseStep recurseStep = stack.Pop();

                if (recurseStep.currentDepth == allWordsList.Length) {
                    Console.WriteLine(recurseStep.currentString);
                } else {
                    List<string> wordList = allWordsList[recurseStep.currentDepth];
                    for (int wordListIndex = 0; wordListIndex < wordList.Count; wordListIndex++) {
                        RecurseStep nextState = new RecurseStep();
                        nextState.currentDepth = recurseStep.currentDepth + 1;
                        nextState.currentWordListIndex = wordListIndex;
                        nextState.currentString = recurseStep.currentString + wordList[wordListIndex];

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
        private static List<string>[] GetWordsLists(List<string> wordPaths, out int[] maxWordLengths) {
            List<string>[] allWordsLists = new List<string>[wordPaths.Count];
            maxWordLengths = new int[wordPaths.Count];
            for (int i = 0; i < allWordsLists.Length; i++) {
                string wordPath = wordPaths[i];
                if (!wordPath.Contains(".txt")) {
                    wordPath = wordPath += ".txt";
                }
                int maxWordLength;
                List<string> wordsList = GetStrings(wordPath, out maxWordLength).ToList<string>();
                if (wordsList == null) {
                    Console.WriteLine("ERROR file " + wordPath + " not found");
                    return null;
                }

                if (wordsList.Count == 0) {
                    Console.WriteLine("WARNING: wordslist for " + wordPath + " is empty");
                }

                maxWordLengths[i] = maxWordLength;
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
                            } else {
                                expandedList.Add(word.ToUpper());
                            }
                        }
                    }//foreach words

                    //tex after above since dont_add_original will clear non alphbetical strings since they have no case
                    if (genConfig.word_variations_all.Contains("blank_optional")) {
                        expandedList.Add("");
                    }

                    allWordsLists[i] = expandedList.ToList<string>();
                    allWordsLists[i].Sort();
                }
            }//if word_variations_all
        }//GenerateWordVariations

        private static string GetPath(string path) {
            if (Directory.Exists(path) || File.Exists(path)) {
                if (!Path.IsPathRooted(path)) {
                    path = Path.GetFullPath(path);
                }
            } else {
                Console.WriteLine("Could not find path " + path);
                path = null;
            }

            return path;
        }

        private static HashSet<string> GetStrings(string path, out int maxWordLength) {
            maxWordLength = 0;
            List<string> files = GetFileList(path);
            if (files == null) {
                return null;
            }

            var strings = new HashSet<string>();
            foreach (string filePath in files) {
                foreach (string line in File.ReadLines(filePath)) {
                    maxWordLength = Math.Max(line.Length, maxWordLength);
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


            RecurseStep startStateS = new RecurseStep();
            startStateS.currentDepth = 0;
            startStateS.currentWordListIndex = 0;
            startStateS.currentString = "";
            GenerateStringSimple2_r(allWordsLists, startStateS);

            GenerateStringSimple_Stack(allWordsLists);
        }//SimpleTests
    }//Program
}
