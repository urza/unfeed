# 1. Vision

You are here: the reason the product exists. Read this before anything else. The overview (chapter 2) turns it into scope, and the architecture (chapter 3) turns the scope into parts.

Source map: [Product overview](../README.md) · [Feed presentation](../src/Feed.Web/Components/FeedView.razor).

## The problem

Social feeds serve the platform, not the reader. Ads, suggested posts, influencer bait and ranking bury the few posts the reader wanted: the posts from friends.

Official platform APIs do not provide the personal Facebook and Instagram reading experience this project needs. Feed v3 therefore reads the pages available to the logged-in owner.

## The idea

A personal agent browses the owner's feeds the way the owner would. It runs a real browser, logged in as the owner, and scrolls at a human pace. It keeps what the owner would have kept. Everything lands on one private page.

The core intuition: if a human can open a browser and scroll, an agent can too. The agent reads through the front door, as the owner.

## What the page does

- It shows posts from friends, newest first. Time decides the order, not an algorithm the platform owns.
- It sorts posts into categories the owner defines. A language model judges each post against the owner's own written policy.
- It lets the owner build views. A view is a category, a list of people, a rule, or a union of other views.
- It explains every decision. A post shows why it is in a view. A hidden post shows who hid it and why.
- It can send one thing back: a heart. The owner presses a heart on the page, and the agent puts a like on the original post.

## Principles

1. **The owner is the customer.** Local-first. The owner controls storage, browser sessions and deployment. Classification uses the chosen model endpoint; a remote endpoint receives the post text and images sent to it. Local hosting keeps model processing on infrastructure the owner controls.
2. **Transparent rules the owner owns.** Every rule is plain text the owner can read and edit. If a post is hidden, the owner can ask why and get an answer.
3. **Categories and views of the owner's choosing.** The owner defines what is interesting. The agent sorts.
4. **The language model is part of the loop.** One model judges posts against the owner's policy. Optionally another model can work as the operating agent and run the system, read its logs and talk to the owner. Or the system can run as an app and only use the first model to judge content.
5. **Browse like a human.** A real browser, the owner's own IP, bounded sessions, a fixed daily cadence, and a full stop on any checkpoint.
6. **Read-only, plus one write.** The agent likes posts on explicit owner action. It never comments, messages or posts.

## Non-goals

- Not a commercial scraper and not a data product.
- No engagement mechanics and no analytics about the owner.
- Not real-time. Fresh a few times a day, or on request, is fresh enough.
- Not multi-user. One instance serves one person.

## Success looks like

The owner opens the page with morning coffee. It shows what friends posted since the last visit, sorted into the owner's views. The owner reaches the end in minutes. The owner can catch up without opening the platforms' ranked feeds; messaging and other platform features remain in their apps.
