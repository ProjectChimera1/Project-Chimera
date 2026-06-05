# Step 1: Context Discovery & Initialization


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

- NEVER generate content without user input
- ALWAYS treat this as collaborative discovery between technical peers
- YOU ARE A FACILITATOR, not a content generator
- FOCUS on discovering existing project context and technology stack
- IDENTIFY critical implementation rules that AI agents need
- ABSOLUTELY NO TIME ESTIMATES
- ✅ YOU MUST ALWAYS SPEAK OUTPUT In your Agent communication style with the config `{communication_language}`

## EXECUTION PROTOCOLS:

- Show your analysis before taking any action
- Read existing project files to understand current context
- Initialize document and update frontmatter
- FORBIDDEN to load next step until discovery is complete

## CONTEXT BOUNDARIES:

- Variables from workflow.md are available in memory
- Focus on existing project files and architecture decisions
- Look for patterns, conventions, and unique requirements
- Prioritize rules that prevent implementation mistakes

## YOUR TASK:

Discover the project's game engine, technology stack, existing patterns, and critical implementation rules that AI agents must follow when writing game code.

## DISCOVERY SEQUENCE:

### 1. Check for Existing Project Context

First, check if project context already exists:

- Look for file at `{output_folder}/project-context.md`
- If exists: Read complete file to understand existing rules
- Present to user: "Found existing project context with {number_of_sections} sections. Would you like to update this or create a new one?"

### 2. Discover Game Engine & Technology Stack

Load and analyze project files to identify technologies:

**Architecture Document:**

- Look for `{output_folder}/game-architecture.md` or `{planning_artifacts}/architecture.md`
- Extract engine choice with specific version (Unity, Unreal, Godot, custom)
- Note architectural decisions that affect implementation

**Engine-Specific Files:**

- Unity: Check for `ProjectSettings/ProjectVersion.txt`, `Packages/manifest.json`
- Unreal: Check for `.uproject` files, `Config/DefaultEngine.ini`
- Godot: Check for `project.godot`, `export_presets.cfg`
- Custom: Check for engine config files, build scripts

**Package/Dependency Files:**

- Unity: `Packages/manifest.json`, NuGet packages
- Unreal: `.Build.cs` files, plugin configs
- Godot: `addons/` directory, GDExtension configs
- Web-based: `package.json`, `requirements.txt`

**Configuration Files:**

- Build tool configs
- Linting and formatting configs
- Testing configurations
- CI/CD pipeline configs

### 3. Identify Existing Code Patterns

Search through existing codebase for patterns:

**Naming Conventions:**

- Script/class naming patterns
- Asset naming conventions
- Scene/level naming patterns
- Test file naming patterns

**Code Organization:**

- How components/scripts are structured
- Where utilities and helpers are placed
- How systems are organized
- Folder hierarchy patterns

**Engine-Specific Patterns:**

- Unity: MonoBehaviour patterns, ScriptableObject usage, serialization rules
- Unreal: Actor/Component patterns, Blueprint integration, UE macros
- Godot: Node patterns, signal usage, autoload patterns

### 4. Extract Critical Implementation Rules

Look for rules that AI agents might miss:

**Engine-Specific Rules:**

- Unity: Assembly definitions, Unity lifecycle methods, coroutine patterns
- Unreal: UPROPERTY/UFUNCTION usage, garbage collection rules, tick patterns
- Godot: `_ready` vs `_enter_tree`, node ownership, scene instancing

**Performance Rules:**

- Frame budget constraints
- Memory allocation patterns
- Hot path optimization requirements
- Object pooling patterns

**Platform-Specific Rules:**

- Target platform constraints
- Input handling conventions
- Platform-specific code patterns
- Build configuration rules

**Testing Rules:**

- Test structure requirements
- Mock usage conventions
- Integration vs unit test boundaries
- Play mode vs edit mode testing

### 5. Initialize Project Context Document

Based on discovery, create or update the context document:

#### A. Fresh Document Setup (if no existing context)

Copy template from `{installed_path}/project-context-template.md` to `{output_folder}/project-context.md`
Initialize frontmatter with:

```yaml
---
project_name: '{{project_name}}'
user_name: '{{user_name}}'
date: '{{date}}'
sections_completed: ['technology_stack']
existing_patterns_found: { { number_of_patterns_discovered } }
---
```

#### B. Existing Document Update

Load existing context and prepare for updates
Set frontmatter `sections_completed` to track what will be updated

### 6. Present Discovery Summary

Report findings to user:

"Welcome {{user_name}}! I've analyzed your game project for {{project_name}} to discover the context that AI agents need.

**Game Engine & Stack Discovered:**
{{engine_and_version}}
{{list_of_technologies_with_versions}}

**Existing Patterns Found:**

- {{number_of_patterns}} implementation patterns
- {{number_of_conventions}} coding conventions
- {{number_of_rules}} critical rules

**Key Areas for Context Rules:**

- {{area_1}} (e.g., Engine lifecycle and patterns)
- {{area_2}} (e.g., Performance and optimization)
- {{area_3}} (e.g., Platform-specific requirements)

{if_existing_context}
**Existing Context:** Found {{sections}} sections already defined. We can update or add to these.
{/if_existing_context}

Ready to create/update your project context. This will help AI agents implement game code consistently with your project's standards.

[C] Continue to context generation"

## SUCCESS METRICS:

- Existing project context properly detected and handled
- Game engine and technology stack accurately identified with versions
- Critical implementation patterns discovered
- Project context document properly initialized
- Discovery findings clearly presented to user
- User ready to proceed with context generation

## FAILURE MODES:

- Not checking for existing project context before creating new one
- Missing critical engine versions or configurations
- Overlooking important coding patterns or conventions
- Not initializing frontmatter properly
- Not presenting clear discovery summary to user

## NEXT STEP:

After user selects [C] to continue, load `./step-02-generate.md` to collaboratively generate the specific project context rules.

Remember: Do NOT proceed to step-02 until user explicitly selects [C] from the menu and discovery is confirmed!
