namespace Gatto.Repl;

//the text /init sends as the user's turn, tools and permissions apply as if it was typed
public static class InitPrompt
{
    public const string Text = """
        Set up GATTO.md for this project. gatto loads GATTO.md into the system prompt of every session started in this directory or below it. Its job: a future session starts work here without the user explaining the project again. If the user has to repeat context at the start of a session, GATTO.md has failed. Make it complete about everything a newcomer would get wrong, and leave out everything a newcomer would get right. It is sent with every request, so every line must earn its place.

        You will survey the project, then interview the user with the ask_user tool, then write the file. The interview is the most important part: the files tell you what the project is, only the user can tell you how it is worked on.

        How to use ask_user:
        - Each call takes 1 to 4 questions; each question has 2 to 4 options. The user can always type a free-text answer instead of picking an option.
        - Turn what you inferred into options, so the user confirms or corrects with one key. When the files point to one answer, list it first and mark it recommended. When you have no evidence, mark nothing recommended.
        - Every question must be one the files cannot answer, or one where the files disagree or look outdated. Never ask what you already know.
        - Ask in rounds. Read each round's answers before you write the next round; follow up where an answer opens something new.
        - If ask_user is not available, ask the same questions as a short numbered list in your reply, stop, and continue when the user answers.

        ## Phase 0: Check for an existing GATTO.md

        Read GATTO.md in the working directory, if it exists (that one file only; do not explore yet). If it exists, ask:
        - "There is already a GATTO.md here. What should /init do?"
          Options: "Improve it" (recommended) | "Replace it" | "Cancel"
          Improve: survey, interview for what changed, then propose edits to the existing file.
          Replace: survey, full interview, then write a new file.
          Cancel: stop here and change nothing.

        ## Phase 1: Survey the project

        Before asking the user anything else, learn what the files can tell you. Read:
        - the README and any docs folder index
        - manifest and build files (package.json, *.csproj, *.sln, Cargo.toml, pyproject.toml, go.mod, pom.xml, Makefile, build scripts)
        - CI configuration, lint and formatter configuration, test configuration
        - .gatto.json, .gitignore, .env.example (never copy values from any .env file)
        - other agents' instruction files: AGENTS.md, CLAUDE.md, .cursor/rules or .cursorrules, .github/copilot-instructions.md, .windsurf/rules or .windsurfrules, .clinerules. gatto does not load AGENTS.md or CLAUDE.md by default, so what matters in them must reach GATTO.md.
        - the top two levels of the directory tree, and the entry points
        - the recent history: git log --oneline -20, to see the commit style and what is being worked on

        Work out, and keep a list of what you are sure of, what you are guessing, and what you could not find:
        - what the project is and who it is for
        - languages, frameworks, package manager, target platforms
        - how to build, run, test (including one single test), lint and format
        - how the code is organised: which area does what, where a newcomer would look for each kind of change
        - conventions that differ from language defaults
        - anything that looks surprising, fragile, generated or dangerous to touch

        ## Phase 2: Interview the user

        Ask in rounds with ask_user. Cover every topic below that the survey did not settle. Skip a topic only when the files settle it beyond doubt. Expect four to six rounds for a real project; a tiny project needs fewer.

        Round 1, confirm the survey. Put your conclusions to the user as options: what the project is, the build command, the test command, the main areas. Correct the list from the answers.

        Round 2, how the work is done:
        - What kind of work happens here most (new features, fixes, refactoring, content, operations)?
        - How is a change verified before it counts as done (which tests, which manual check, which command)?
        - What is the commit and branch practice (message style, who commits, is there review)?
        - Should gatto run the build and the tests itself to check its work, or leave that to the user?

        Round 3, boundaries:
        - Which files or folders must gatto never edit (generated code, vendored code, migrations, lock files, secrets)?
        - Which actions need the user's approval first (installing packages, deleting files, pushing, touching production, running migrations)?
        - Are there environments, servers or services gatto must know about, and which of them must it never touch?

        Round 4, traps and knowledge:
        - What goes wrong for a newcomer here? What mistake do people make most?
        - Which words in this project mean something specific (domain terms, internal names, abbreviations)?
        - Is there a decision that looks wrong but is deliberate, which gatto must not "fix"?

        Round 5, follow-ups. Ask about anything the earlier answers opened, anything still marked as a guess, and anything the files and the user disagree on.

        On the "Improve it" path, start with one question: "What has changed since this GATTO.md was written?" with options "New commands or tools" | "New conventions or rules" | "Things in it are wrong" | "Nothing I know of". Then interview only for what the answer and the survey point to.

        ## Phase 3: Propose, then write

        Before writing, print an outline of the file as normal text: each section with a one-line summary of what it will say. Then ask "Does this outline cover it?" with options "Yes, write it" (recommended) | "Something is missing" | "Something should go". Adjust until the user says yes.

        Write GATTO.md in the working directory. Start it with the line: # GATTO.md

        Use these sections, in this order, and leave out a section that has nothing true to say:
        - **What this is**: two or three sentences: what the project does, for whom, and its current state.
        - **Map**: where each kind of change goes; which area does what. A map of responsibilities, not a file listing.
        - **Commands**: build, run, test, a single test, lint, format; exact command lines, and the directory to run them from.
        - **Workflow**: how a change is verified, when it counts as done, the commit and branch practice, what gatto may do on its own and what needs approval first.
        - **Conventions**: rules that differ from the language defaults, each stated specifically ("2-space indentation in TypeScript", not "format properly").
        - **Boundaries**: files and folders gatto must not edit, services and environments it must not touch, and why.
        - **Traps**: what goes wrong for a newcomer, and the deliberate decisions that look like mistakes.
        - **Terms**: project words with their meaning here.

        Rules for the content:
        - Every line must pass this test: "Would removing this make gatto make a mistake or ask the user again?" If not, cut it.
        - Write only what you read in the files or what the user told you. Where they disagree, the user wins; say which file is out of date.
        - Never write a secret, key, token, password or private address into GATTO.md. Name the file or the place that holds it instead.
        - Leave out what changes often: versions, dates, counts, the current branch, open work. Name the file that holds it instead.
        - Leave out long references, tutorials and API documentation. Name the path, so a session can read it when it needs it.
        - Leave out standard language conventions, generic advice, and commands that are obvious from the manifest.
        - Do not repeat what another GATTO.md already in your system prompt says.
        - Do not invent sections or advice the user did not give and the files do not show.

        On the "Improve it" path: do not rewrite the file. Propose specific additions, changes and removals as diffs, each with a one-line reason. Keep every <!-- --> comment exactly as written: gatto strips those comments before the file reaches the model, so they are the user's own notes. Then ask "Apply these edits?" with options "Apply all" (recommended) | "Let me pick" | "Leave the file as is", and write only what the user approves.

        For a project with distinct parts (a monorepo, several modules): say that a subfolder can hold its own GATTO.md, which applies to sessions started in that subfolder, and offer to write one for each part that needs different instructions.

        ## Phase 4: Check the file, then report

        Read GATTO.md back. Check each command line against the file it came from, and each path against the tree. Fix anything that does not match.

        Then report: the file written, its sections in one line each, and what you still could not settle. Add a short list of improvements that would help gatto work here, most useful first, including only what applies: missing tests (gatto cannot verify its own changes without them), no linter or formatter configuration, no single documented build command, the GitHub CLI missing while the project is on GitHub. Say that /init can be run again at any time to update the file.
        """;
}
