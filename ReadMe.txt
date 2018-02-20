BruteGen
Tool for generating strings for dictionary attacks, hash optional testing vs Fox Engine hashes.

Usage
--------
BruteGen <config .json>

See Examples folder for config examples.

Notes:
--------
.json doesn't allow comments so will describe config here.

{
  "batch_size": 1,
  //tex number of strings to generate before writing to output (both disk and console), resume state will also be saved. 
  The time it will take to complete a batch also depends on how many words and the length of the word lists. 
  There's also a trade off between how often it saves progress vs the i/o overhead of doing so. 
  While initially bulding an attack config I often have this low enough so it updates every few seconds, then when I think I've got the config correct I'll increase it to hit ~10-20 seconds between updates.  


  "words_base_path": "D:\\GitHub\\BruteGen\\Examples\\3x3 - test hashes",
  //tex used to set working path to allow words_paths be relative (and shorter)
  //.json requires backslashes to be escaped with a backslash

  "words_paths": ["wordA", "wordB", "wordC"],
  //tex path to file of strings per word, actual file must have .txt extension, but path provided here can be without, the loading method will deal with it.

  "word_variations_all": [
    "dont_add_original",
    "all_upper",
    "all_lower",
    "capitalized",
    "blank_optional",
  ],
  //tex will add a variation for each string in a wordlist
  dont_add_original removes the original string in the wordlist, so it should only be used in addition to other varations. As a side effect currently will remove strings that have no alphabet characters
  blank_optional adds an empty string to each wordlist (not each string in each wordlist), this essentially makes each word have an optional case.

  "output_path": "D:\\GitHub\\BruteGen\\Examples\\Examples Output",
  //tex optional, will fall back to wordspath, path to write strings and resume_state to.

  "test_hashes_path": "D:\\GitHub\\BruteGen\\Examples\\3x3 - test hashes\\StrCode32Hashes",
  //tex optional, folder or file path of hashes to test against
  
  "test_hashes_func": "StrCode32"
  //tex optional if not using test_hashes_path, name of hash function to use (see the HashWrangler tool).
}

Strings will be written to <output_path>\<config file name>.txt

Resuming:
Each batch update will save -resume_state.json to output_path, allowing a run on a config to be resumed. Simply close the program after a batch update and run the same config again. 
It's generally a bad idea to resume a config after changine it's .json settings.
If you wish to restart the run for the config you must delete the resume_state file. 
It will be automatically deleted if the program completes it's run normally.
When resuming the output strings file will be appended to, and will overwrite the output strings if not resuming.

Hash testing:
When using test_hashes BruteGen will output all matches, you should use HashWrangler to validate against existing dictionaries and/or to resolve any hash collisions.

General tips:
You can have a blank line in a words.txt to make that word generate optionally.

If you're trying to generate a string like:
an_example_string

You would have a words file for the seperator:
underscore.txt >
_


"words_paths": ["wordA", "underscore", "wordB", "underscore", "wordC"],

If you're trying to generate a sequence of numbers you would have a words file with 0-9, and just repeat it as many times as nessesary.

SOMESTRING_000

number.txt >
0
1
2
3
...

"words_paths": ["wordA", "underscore", "number", "number", "number"],