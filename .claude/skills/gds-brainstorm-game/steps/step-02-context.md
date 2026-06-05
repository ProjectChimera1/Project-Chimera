---
name: 'step-02-context'
description: 'Load game-specific brainstorming context and techniques'

# Path Definitions
workflow_path: '{installed_path}'

# File References
thisStepFile: './step-02-context.md'
nextStepFile: './step-03-ideation.md'
workflowFile: '{workflow_path}/workflow.md'
outputFile: '{output_folder}/brainstorming-session-{date}.md'

# Context Files
gameContext: '{workflow_path}/game-context.md'
gameBrainMethods: '{workflow_path}/game-brain-methods.csv'
coreBrainstorming: 'skill:bmad-brainstorming'
---

# Step 2: Load Context

**Progress: Step 2 of 4** - Next: Ideation Session

## STEP GOAL:

Load game-specific brainstorming context and techniques to guide the ideation session. Merge game techniques with core brainstorming methods.


## AUTONOMOUS MODE OVERRIDE (READ BEFORE ALL OTHER RULES -- NO EXCEPTIONS)

If `project-intent.md` exists anywhere in the project, **AUTONOMOUS MODE IS UNCONDITIONALLY ACTIVE**. This applies regardless of how this skill was invoked -- by the orchestrator, by the user directly, or any other means. There is no "interactive override" of this rule.

In AUTONOMOUS MODE:
- Immediately check for and read `project-intent.md`
- ALL "NEVER generate content without user input" directives are cancelled -- use project-intent.md as the source of user input
- ALL "halt and wait for user selection" directives are cancelled -- select the best option from project-intent.md and continue immediately
- ALL "Do NOT proceed until user explicitly selects [C]" directives are cancelled -- select C immediately and continue
- ALL "ALWAYS halt at menus" directives are cancelled -- process menus internally, never surface them to the human
- Execute this step fully, write all output, update frontmatter, then **immediately read and execute the next step file in the same response**
- **NEVER end your response at a [C] prompt, a confirmation checkpoint, or a menu.** These are not stopping points in autonomous mode -- they are auto-confirmed. Keep going.
- Only end your response when the FINAL step of the entire workflow is fully complete and all output files are written
- Complete the entire workflow to its final step in a single response

**Do NOT reason about whether this invocation is "interactive" -- if project-intent.md exists, autonomous mode is active, full stop.**

The MANDATORY EXECUTION RULES below are overridden by the above when project-intent.md is present.

## MANDATORY EXECUTION RULES (READ FIRST):

### Universal Rules:

- NEVER generate content without user input
- CRITICAL: Read the complete step file before taking any action
- CRITICAL: When loading next step with 'C', ensure entire file is read
- YOU ARE A FACILITATOR, not a content generator
- NEVER mention time estimates
- ✅ YOU MUST ALWAYS SPEAK OUTPUT In your Agent communication style with the config `{communication_language}`

### Role Reinforcement:

- You are a creative game design facilitator
- Game-specific techniques enhance standard brainstorming
- Understand various ideation methods deeply

### Step-Specific Rules:

- Load all context files completely
- Present technique options to user
- Let user select preferred approach

## EXECUTION PROTOCOLS:

- Show your analysis before taking any action
- Present A/P/C menu after context loaded
- ONLY proceed when user chooses C (Continue)
- Update frontmatter `stepsCompleted: [1, 2]` before loading next step

## COLLABORATION MENUS (A/P/C):

- **A (Advanced Elicitation)**: Explore technique combinations
- **P (Party Mode)**: Get perspectives on approaches
- **C (Continue)**: Confirm context and proceed

## Sequence of Instructions (Do not deviate, skip, or optimize)

### 1. Load Game Context

**Load the game context document:**

Read `{gameContext}` to understand:

- Focus areas for game ideation
- Key considerations for game design
- Recommended techniques
- Output structure guidance

### 2. Load Game Brain Methods

**Load game-specific techniques:**

Read `{gameBrainMethods}` CSV to load:

- MDA Framework exploration
- Core loop brainstorming
- Player fantasy mining
- Genre mashup
- And other game-specific methods

### 3. Present Available Techniques

"**Game Brainstorming Techniques Loaded!**

I've loaded game-specific brainstorming methods:

**Conceptual Techniques:**

- **MDA Framework** - Mechanics, Dynamics, Aesthetics exploration
- **Player Fantasy Mining** - What fantasy does the player fulfill?
- **Core Loop Design** - Define the central gameplay loop
- **Genre Mashup** - Combine unexpected genres

**Experience Techniques:**

- **Emotion Mapping** - Target emotions throughout gameplay
- **Moment Design** - Plan memorable peak moments
- **Flow Analysis** - Balance challenge and skill

**Practical Techniques:**

- **Constraint Box** - Creative limits spark innovation
- **Reference Blending** - Combine inspiration sources
- **What If Scenarios** - Explore radical possibilities

**How would you like to brainstorm?**

1. **Guided** - I'll walk you through techniques one by one
2. **Selective** - Choose specific techniques to use
3. **Freeform** - Open exploration with techniques as needed
4. **YOLO** - Let me drive the session with all techniques

Your preference:"

### 4. Capture User Preference

**Based on selection:**

- **Guided**: Prepare structured technique sequence
- **Selective**: Present technique menu for selection
- **Freeform**: Prepare all techniques for on-demand use
- **YOLO**: Plan comprehensive technique coverage

### 5. Generate Context Section

Based on the conversation, prepare the content:

```markdown
## Brainstorming Approach

**Selected Mode:** {{selected_mode}}

**Techniques Available:**
{{technique_list}}

**Focus Areas:**
{{focus_areas_from_context}}
```

### 6. Present Content and Menu

Show the loaded context and present:

"I've prepared the brainstorming context.

**Here's what I'll add to the document:**

[Show the complete markdown content from step 5]

**Ready to start ideation?**

**Select an Option:**
[A] Advanced Elicitation - Explore technique combinations
[P] Party Mode - Get perspectives on approaches
[C] Continue - Save this and move to Ideation Session (Step 3 of 4)"

### 7. Handle Menu Selection

#### IF A (Advanced Elicitation):

- Explore technique combinations and synergies
- Ask user: "Accept these changes? (y/n)"
- If yes: Update content, return to A/P/C menu
- If no: Keep original, return to A/P/C menu

#### IF P (Party Mode):

- Get multiple perspectives on brainstorming approaches
- Ask user: "Accept these changes? (y/n)"
- If yes: Update content, return to A/P/C menu
- If no: Keep original, return to A/P/C menu

#### IF C (Continue):

- Append the context section to `{outputFile}`
- Update frontmatter: `stepsCompleted: [1, 2]`
- Load `{nextStepFile}`

## CRITICAL STEP COMPLETION NOTE

ONLY WHEN [C continue option] is selected and [context saved with frontmatter updated], will you then load and read fully `{nextStepFile}`.

---

## SYSTEM SUCCESS/FAILURE METRICS

### SUCCESS:

- Game context loaded completely
- Game brain methods loaded from CSV
- Techniques presented clearly
- User selected brainstorming approach
- A/P/C menu presented and handled correctly
- Frontmatter updated with stepsCompleted: [1, 2]

### SYSTEM FAILURE:

- Not loading context files
- Proceeding without user technique selection
- Not presenting A/P/C menu after context
- Proceeding without user selecting 'C'

**Master Rule:** Skipping steps, optimizing sequences, or not following exact instructions is FORBIDDEN and constitutes SYSTEM FAILURE.
