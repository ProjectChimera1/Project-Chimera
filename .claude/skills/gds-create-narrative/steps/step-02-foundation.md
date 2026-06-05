---
name: 'step-02-foundation'
description: 'Define narrative premise, themes, tone, and story structure'

# Path Definitions
workflow_path: '{installed_path}'

# File References
thisStepFile: './step-02-foundation.md'
nextStepFile: './step-03-story.md'
workflowFile: '{workflow_path}/workflow.md'
outputFile: '{output_folder}/narrative-design.md'

# Task References
advancedElicitationTask: 'skill:bmad-advanced-elicitation'
partyModeWorkflow: 'skill:bmad-party-mode'
---

# Step 2: Story Foundation

**Progress: Step 2 of 11** - Next: Story Beats

## STEP GOAL:

Define the narrative foundation: premise, themes, tone/atmosphere, and overall story structure. These elements form the backbone of all narrative content.


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
- ✅ YOU MUST ALWAYS SPEAK OUTPUT In your Agent communication style with the config `{communication_language}`
- NEVER mention time estimates

### Role Reinforcement:

- You are a narrative design facilitator
- Help users articulate THEIR story vision
- The premise should come from the user

### Step-Specific Rules:

- FORBIDDEN to generate premise without user input
- Draw out user's ideas through questions
- Themes should resonate with user's intent

## EXECUTION PROTOCOLS:

- Show your analysis before taking any action
- Present A/P/C menu after generating content
- ONLY save when user chooses C (Continue)
- Update frontmatter `stepsCompleted: [1, 2]` before loading next step

## COLLABORATION MENUS (A/P/C):

- **A (Advanced Elicitation)**: Explore themes deeper
- **P (Party Mode)**: Get perspectives on foundation
- **C (Continue)**: Save the content and proceed

## Sequence of Instructions (Do not deviate, skip, or optimize)

### 1. Narrative Premise Discovery

"Let's define the narrative foundation for **{{game_name}}**.

**First, the premise - your story's elevator pitch in 2-3 sentences.**

Good premises have:

- A protagonist with a clear goal
- An obstacle or conflict
- Stakes (what happens if they fail?)

**Examples:**

- \"A young knight discovers they're the last hope to stop an ancient evil, but must choose between saving the kingdom or their own family.\"
- \"After a mysterious pandemic, survivors must navigate a world where telling the truth is deadly but lying corrupts your soul.\"

What's the premise for {{game_name}}?"

### 2. Theme Discovery

"**Now let's identify your core themes.**

Themes are the underlying ideas or messages woven throughout the story.

**Common game themes:**

- Redemption, sacrifice, identity
- Power and corruption
- Hope vs. despair
- Nature vs. technology
- Freedom vs. control
- Family, loyalty, betrayal

**Questions to consider:**

- What questions does your story ask?
- What will players think about after playing?
- What emotions do you want to evoke?

What are 2-4 core themes for {{game_name}}?"

### 3. Tone and Atmosphere Discovery

"**Let's define the tone and atmosphere.**

Tone shapes how the story feels moment-to-moment.

**Tone spectrums:**

- Dark ←→ Lighthearted
- Serious ←→ Comedic
- Gritty ←→ Fantastical
- Intimate ←→ Epic
- Hopeful ←→ Melancholic

**Atmosphere elements:**

- Visual mood (colors, lighting)
- Audio mood (music style)
- Pacing (contemplative vs. urgent)
- Emotional register

Describe the tone and atmosphere for {{game_name}}:"

### 4. Story Structure Discovery

"**What story structure will you use?**

**Common structures:**

| Structure          | Description                                            |
| ------------------ | ------------------------------------------------------ |
| **3-Act**          | Setup → Confrontation → Resolution                     |
| **Hero's Journey** | Campbell's monomyth (departure, initiation, return)    |
| **Kishōtenketsu**  | 4-act: Introduction → Development → Twist → Conclusion |
| **Episodic**       | Self-contained episodes with overarching arc           |
| **Branching**      | Multiple paths and endings                             |
| **Freeform**       | Player-driven, emergent narrative                      |

What structure fits {{game_name}}?"

### 5. Act Breakdown

"**Let's break down your story into acts/sections.**

Based on {{selected_structure}}:

{{structure_specific_prompts}}

Describe each act/section for {{game_name}}:"

### 6. Generate Foundation Content

Based on the conversation, prepare the content:

```markdown
## Story Foundation

### Narrative Premise

{{user_premise}}

### Core Themes

{{themes_list_with_descriptions}}

### Tone and Atmosphere

**Tone:** {{tone_description}}

**Atmosphere:** {{atmosphere_description}}

**Emotional Register:** {{emotional_goals}}

---

## Story Structure

### Structure Type

**{{structure_type}}**

{{structure_description}}

### Act Breakdown

{{act_breakdown_details}}
```

### 7. Present Content and Menu

Show the generated content to the user and present:

"I've drafted the Story Foundation based on our conversation.

**Here's what I'll add to the document:**

[Show the complete markdown content from step 6]

**Validation Check:**

- Does the premise capture your vision?
- Do the themes resonate with your intent?
- Does the structure fit your gameplay?

**Select an Option:**
[A] Advanced Elicitation - Explore themes and structure deeper
[P] Party Mode - Get perspectives on foundation
[C] Continue - Save this and move to Story Beats (Step 3 of 11)"

### 8. Handle Menu Selection

#### IF A (Advanced Elicitation):

- Execute {advancedElicitationTask} with the current content
- Ask user: "Accept these changes? (y/n)"
- If yes: Update content, return to A/P/C menu
- If no: Keep original, return to A/P/C menu

#### IF P (Party Mode):

- Execute {partyModeWorkflow} with the current content
- Ask user: "Accept these changes? (y/n)"
- If yes: Update content, return to A/P/C menu
- If no: Keep original, return to A/P/C menu

#### IF C (Continue):

- Append the final content to `{outputFile}`
- Update frontmatter: `stepsCompleted: [1, 2]`
- Load `{nextStepFile}`

## CRITICAL STEP COMPLETION NOTE

ONLY WHEN [C continue option] is selected and [foundation content saved with frontmatter updated], will you then load and read fully `{nextStepFile}`.

---

## SYSTEM SUCCESS/FAILURE METRICS

### SUCCESS:

- Premise captured from user input
- Themes identified and described
- Tone and atmosphere defined
- Story structure selected and broken down
- A/P/C menu presented and handled correctly
- Frontmatter updated with stepsCompleted: [1, 2]

### SYSTEM FAILURE:

- Generating premise FOR user
- Generic themes not connected to user's vision
- Proceeding without structure breakdown
- Not presenting A/P/C menu after content
- Proceeding without user selecting 'C'

**Master Rule:** Skipping steps, optimizing sequences, or not following exact instructions is FORBIDDEN and constitutes SYSTEM FAILURE.
