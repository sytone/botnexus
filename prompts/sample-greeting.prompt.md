---
name: sample-greeting
description: Simple multi-line greeting template
parameters:
  name:
    description: Name of the person to greet
    required: true
  greeting_style:
    description: "Style of greeting: formal, casual, or friendly"
    default: friendly
    required: false
---
Hello {{name}}!

This is a {{greeting_style}} greeting from BotNexus.

Thank you for using our prompt template system. The `.prompt.md` format makes it easy to author multi-line, readable prompts with structure and personality.

Best regards,
The BotNexus Team
