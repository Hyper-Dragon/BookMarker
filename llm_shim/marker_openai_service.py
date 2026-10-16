"""marker LLM service: OpenAIService with a structured-output fallback.

marker makes every LLM call through the OpenAI SDK's structured-output helper
(`client.beta.chat.completions.parse` -> response_format json_schema). Some
OpenAI-compatible backends reject json_schema (e.g. `400 This response_format
type is unavailable now` from models that only support json_object). Translate
on the client side:

  attempt 1: strict json_schema (unchanged - keeps enforcement when the backend supports it)
  attempt 2: response_format={"type": "json_object"} + the JSON schema inlined into
             the prompt, then json.loads() the content (marker does json.loads on the
             content anyway, so the return contract is unchanged)

Mounted into the container at /app/shim (PYTHONPATH) and selected with
`--llm_service marker_openai_service.OpenAICompatibleService`.
"""

import json
import time

import openai
import PIL
from openai import APITimeoutError, RateLimitError

from marker.logger import get_logger
from marker.schema.blocks import Block
from marker.services.openai import OpenAIService

logger = get_logger()

_SCHEMA_INSTRUCTION = (
    "\n\nRespond with ONLY a single valid JSON document that matches this JSON schema "
    "(no code fences, no commentary):\n"
)


class OpenAICompatibleService(OpenAIService):
    """OpenAIService with a json_object fallback for json_schema-rejecting backends.

    Process-wide latch: after the first strict-json_schema failure we stop paying
    for a doomed strict attempt on every call (a json_object-only backend always
    fails it); a schema-capable backend never flips the latch and keeps the
    strict path for the whole run.
    """

    _json_schema_unsupported = False

    def _call(self, client, messages, response_schema, timeout, max_retries, mode):
        total_tries = max_retries + 1
        for tries in range(1, total_tries + 1):
            try:
                if mode == "schema":
                    return client.beta.chat.completions.parse(
                        extra_headers={
                            "X-Title": "Marker",
                            "HTTP-Referer": "https://github.com/datalab-to/marker",
                        },
                        model=self.openai_model,
                        messages=messages,
                        timeout=timeout,
                        response_format=response_schema,
                    )
                return client.chat.completions.create(
                    extra_headers={
                        "X-Title": "Marker",
                        "HTTP-Referer": "https://github.com/datalab-to/marker",
                    },
                    model=self.openai_model,
                    messages=messages,
                    timeout=timeout,
                    response_format={"type": "json_object"},
                )
            except (APITimeoutError, RateLimitError) as e:
                if tries == total_tries:
                    logger.error(
                        f"[{mode}] rate limit/timeout, giving up: {e} "
                        f"(attempt {tries}/{total_tries})"
                    )
                    return None
                wait_time = tries * self.retry_wait_time
                logger.warning(
                    f"[{mode}] rate limit/timeout: {e}. Retrying in {wait_time}s "
                    f"(attempt {tries}/{total_tries})"
                )
                time.sleep(wait_time)
            except Exception as e:
                logger.warning(f"[{mode}] call failed: {e}")
                return None
        return None

    def __call__(
        self,
        prompt: str,
        image: PIL.Image.Image | list | None,
        block: Block | None,
        response_schema,
        max_retries: int | None = None,
        timeout: int | None = None,
    ):
        if max_retries is None:
            max_retries = self.max_retries
        if timeout is None:
            timeout = self.timeout

        client = self.get_client()
        image_data = self.format_image_for_llm(image)

        def messages_for(text):
            return [
                {
                    "role": "user",
                    "content": [*image_data, {"type": "text", "text": text}],
                }
            ]

        # 1) strict structured output (json_schema) - preserved when a capable
        #    model serves; skipped once the backend has proven it rejects it.
        if type(self)._json_schema_unsupported:
            response = None
        else:
            response = self._call(
                client, messages_for(prompt), response_schema, timeout, max_retries, "schema"
            )

        # 2) fallback: json_object + schema in prompt (for backends that reject json_schema)
        if response is None or response.choices[0].message.content is None:
            if not type(self)._json_schema_unsupported:
                type(self)._json_schema_unsupported = True
                logger.warning(
                    "structured call failed - falling back to json_object mode with schema"
                    " in prompt (latching: strict json_schema attempts skipped for this process)"
                )
            try:
                schema_text = json.dumps(response_schema.model_json_schema())
            except Exception as e:
                schema_text = "{}"
                logger.error(f"could not render response schema for fallback prompt: {e}")
            response = self._call(
                client,
                messages_for(prompt + _SCHEMA_INSTRUCTION + schema_text),
                response_schema,
                timeout,
                max_retries,
                "json_object",
            )

        if response is None or response.choices[0].message.content is None:
            return {}

        if block is not None:
            usage = getattr(response, "usage", None)
            if usage is not None:
                block.update_metadata(
                    llm_tokens_used=usage.total_tokens, llm_request_count=1
                )

        try:
            return json.loads(response.choices[0].message.content)
        except Exception as e:
            logger.error(f"could not parse JSON from response: {e}")
            return {}
