# llm_shim

`marker_openai_service.py` is a drop-in marker LLM service module: an
`OpenAIService` subclass that keeps the strict structured-output (`json_schema`)
call and falls back to `json_object` + schema-in-prompt for OpenAI-compatible
backends that reject `json_schema`.

The container mounts this directory at `/app/shim` (on `PYTHONPATH`) and selects it with:

```text
marker ... --use_llm --llm_service marker_openai_service.OpenAICompatibleService --config_json <cfg>
```

The config file supplies `openai_base_url`, `openai_model`, `openai_api_key`,
`timeout`, `max_retries`, `retry_wait_time`.
