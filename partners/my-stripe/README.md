service which looks like a stipe but imitates a real card processor without moving any money

will support webhook sripe-shaped json, signed with the same HMAC scheme

should support deterministic test requests
for example if idempotency key containes "fail" - request will return "declined"
or amount ends in "02" - request will return "pending"

in this case we can create fully deterministic tests

same for refund functional

